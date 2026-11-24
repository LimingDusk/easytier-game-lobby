<xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:svg="http://www.w3.org/2000/svg">
  <xsl:output method="text" encoding="utf-8"/>
  <xsl:template match="/svg:svg[svg:symbol]">
    <xsl:text>// Generated from LobbyIcons.svg.&#10;namespace GameLobby;&#10;internal static class SvgPaths&#10;{&#10;    internal static readonly System.Collections.Generic.Dictionary&lt;string, string&gt; Data = new()&#10;    {&#10;</xsl:text>
    <xsl:for-each select="svg:symbol">
      <xsl:text>        ["</xsl:text><xsl:value-of select="@id"/><xsl:text>"] = "</xsl:text><xsl:value-of select="svg:path/@d"/><xsl:text>",&#10;</xsl:text>
    </xsl:for-each>
    <xsl:text>    };&#10;}&#10;</xsl:text>
  </xsl:template>
  <xsl:template match="/svg:svg[svg:rect]">
    <xsl:text>// Generated from GameLobby.svg.&#10;namespace GameLobby;&#10;internal static class Brand&#10;{&#10;</xsl:text>
    <xsl:text>    internal const string Path = "</xsl:text><xsl:value-of select="svg:path/@d"/><xsl:text>";&#10;</xsl:text>
    <xsl:text>    internal const string Fill = "</xsl:text><xsl:value-of select="svg:rect/@fill"/><xsl:text>";&#10;</xsl:text>
    <xsl:text>    internal const double Size = </xsl:text><xsl:value-of select="svg:rect/@width"/><xsl:text>;&#10;</xsl:text>
    <xsl:text>    internal const double Radius = </xsl:text><xsl:value-of select="svg:rect/@rx"/><xsl:text>;&#10;</xsl:text>
    <xsl:text>    internal const double Stroke = </xsl:text><xsl:value-of select="svg:path/@stroke-width"/><xsl:text>;&#10;}&#10;</xsl:text>
  </xsl:template>
</xsl:stylesheet>
