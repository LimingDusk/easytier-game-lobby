"""Run a loopback-only EasyTier capacity check with the official core and CLI."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack
import json
import math
import os
from pathlib import Path
import secrets
import socket
import socketserver
import subprocess
import threading
import time

BLOCK = secrets.token_bytes(65536)
BLOCKS = 128  # 8 MiB per guest per direction


def read_exact(conn, length):
    result = bytearray()
    while len(result) < length:
        data = conn.recv(length - len(result))
        if not data:
            raise EOFError("Transfer ended early")
        result.extend(data)
    return result


class Server(socketserver.ThreadingTCPServer):
    request_queue_size = 64
    daemon_threads = True


class Endpoint(socketserver.BaseRequestHandler):
    def handle(self):
        conn = self.request
        conn.settimeout(12)
        mode = read_exact(conn, 1)
        if mode == b'U':
            for _ in range(BLOCKS):
                if read_exact(conn, len(BLOCK)) != BLOCK:
                    raise ValueError("Upload content mismatch")
            conn.sendall(b'K')
        elif mode == b'D':
            for _ in range(BLOCKS):
                conn.sendall(BLOCK)
        elif mode == b'E':
            while data := conn.recv(256):
                conn.sendall(data)


def stop(proc):
    if proc.poll() is None:
        proc.terminate()
        try:
            proc.wait(5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()


def run(options):
    suffix = '.exe' if os.name == 'nt' else ''
    directory = options.engine_dir.resolve()
    cli = directory / ('easytier-cli' + suffix)
    core = directory / ('easytier-core' + suffix)
    names = {f'player-{i}': f'10.86.0.{i+1}' for i in range(options.nodes)}
    if options.relay:
        names['relay'] = '10.86.0.250'
    allocated = set()

    def port():
        while True:
            with socket.socket() as sock:
                sock.bind(('127.0.0.1', 0))
                number = sock.getsockname()[1]
            if number not in allocated:
                allocated.add(number)
                return number

    def roster(rpc):
        p = subprocess.run([cli, '-p', f'127.0.0.1:{rpc}', '-o', 'json', 'peer'],
                           capture_output=True, timeout=5)
        return json.loads(p.stdout) if p.returncode == 0 else []

    def wait_rosters(active):
        expected = {name: names[name] for name in active}
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            if all({r['hostname']: r['ipv4'] for r in roster(rpcs[name])} == expected for name in active):
                return
            time.sleep(0.5)
        raise TimeoutError(f'Member tables failed to converge to {len(active)} entries')

    with Server(('127.0.0.1', 0), Endpoint) as server, ExitStack() as cleanup:
        threading.Thread(target=server.serve_forever, daemon=True).start()
        cleanup.callback(server.shutdown)
        env = dict(os.environ, ET_NETWORK_SECRET=secrets.token_hex(24))
        network = secrets.token_hex(12)
        rpcs, processes, forwards = {}, {}, []
        hub = f'tcp://127.0.0.1:{port()}'
        for name, ip in names.items():
            rpcs[name] = port()
            args = [core, '--hostname', name, '--ipv4', ip + '/24', '--network-name', network,
                    '--rpc-portal', f'127.0.0.1:{rpcs[name]}', '--private-mode', 'true',
                    '--no-tun', '--use-smoltcp', '--disable-upnp', '--disable-p2p',
                    '--disable-ipv6', '--stun-servers', '--stun-servers-v6']
            if name == ('relay' if options.relay else 'player-0'):
                args.extend(['--listeners', hub])
            else:
                args.extend(['--no-listener', '--peers', hub])
            if name == 'player-0':
                args.extend(['--proxy-networks', '127.0.0.1/32->198.18.0.1/32'])
            elif name != 'relay':
                local = port()
                forwards.append(local)
                args.extend(['--port-forward', f'tcp://127.0.0.1:{local}/198.18.0.1:{server.server_address[1]}'])
            processes[name] = subprocess.Popen(args, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            cleanup.callback(stop, processes[name])
        wait_rosters(names)
        last = f'player-{options.nodes-1}'
        stop(processes[last])
        wait_rosters([name for name in names if name != last])
        restarted = subprocess.Popen(processes[last].args, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        cleanup.callback(stop, restarted)
        wait_rosters(names)
        route = 'relay(2)' if options.relay else 'p2p'
        for guest in range(1, options.nodes):
            host = next(r for r in roster(rpcs[f'player-{guest}']) if r['hostname'] == 'player-0')
            if host['cost'] != route:
                raise RuntimeError(f"Unexpected host route {host['cost']}")

        def transfer(local):
            barrier.wait(timeout=12)
            start = time.monotonic()
            with socket.create_connection(('127.0.0.1', local), timeout=12) as conn:
                conn.sendall(mode)
                if mode == b'U':
                    for _ in range(BLOCKS):
                        conn.sendall(BLOCK)
                    if read_exact(conn, 1) != b'K':
                        raise ValueError("Upload not acknowledged")
                elif mode == b'D':
                    for _ in range(BLOCKS):
                        if read_exact(conn, len(BLOCK)) != BLOCK:
                            raise ValueError("Download content mismatch")
                else:
                    samples = []
                    for i in range(options.seconds):
                        payload = secrets.token_bytes(256)
                        tick = time.monotonic()
                        conn.sendall(payload)
                        if read_exact(conn, len(payload)) != payload:
                            raise ValueError("Echo mismatch")
                        samples.append((time.monotonic() - tick) * 1000)
                        time.sleep(max(0, start + i + 1 - time.monotonic()))
                    return samples
            return time.monotonic() - start

        report = dict(players=options.nodes, route=route, rejoin=True)
        with ThreadPoolExecutor(max_workers=len(forwards)) as pool:
            for mode, key in [(b'U', 'upload'), (b'D', 'download'), (b'E', 'echo')]:
                barrier = threading.Barrier(len(forwards))
                start = time.monotonic()
                values = list(pool.map(transfer, forwards))
                if mode == b'E':
                    index = math.ceil(options.seconds * 0.95) - 1
                    report[key] = dict(replies=sum(map(len, values)),
                                       worst_guest_p95_ms=round(max(sorted(v)[index] for v in values), 2))
                else:
                    megabits = len(BLOCK) * BLOCKS * 8 / 1e6
                    report[key] = dict(aggregate_mbps=round(megabits * len(values) / (time.monotonic()-start), 1),
                                       slowest_guest_mbps=round(megabits/max(values), 1))
        wait_rosters(names)
        print(json.dumps(report), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--engine-dir', required=True, type=Path)
    parser.add_argument('--nodes', type=int, choices=range(2, 21), default=20)
    parser.add_argument('--relay', action='store_true')
    parser.add_argument('--seconds', type=int, choices=range(1, 601), default=60)
    run(parser.parse_args())
