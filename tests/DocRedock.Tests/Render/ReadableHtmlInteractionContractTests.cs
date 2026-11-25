namespace DocRedock.Tests.Render;

using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DocRedock.Render;

public class ReadableHtmlInteractionContractTests
{
    [Fact]
    public void CspHash_IsSha256Base64OfScriptUtf8()
    {
        var expected = "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(ReadableHtmlInteractions.Script)));
        Assert.Equal(expected, ReadableHtmlInteractions.CspHash);
        Assert.StartsWith("sha256-", ReadableHtmlInteractions.CspHash);
    }

    [Fact]
    public void Script_IsSelfContainedStrictIife()
    {
        var script = ReadableHtmlInteractions.Script;
        Assert.StartsWith("(function ()", script);
        Assert.EndsWith("})();", script.TrimEnd());
        Assert.Contains("'use strict'", script);
    }

    [Fact]
    public void Script_ForbidsExternalLoadsEvalAndHtmlInjection()
    {
        var script = ReadableHtmlInteractions.Script;
        foreach (var forbidden in new[]
        {
            "eval(", "Function(", "innerHTML", "outerHTML", "document.write",
            "fetch(", "XMLHttpRequest", "importScripts", "EventSource", "WebSocket",
            "<script", "javascript:", "http://", "https://", "@import",
        })
        {
            Assert.DoesNotContain(forbidden, script, StringComparison.Ordinal);
        }
        Assert.Contains("textContent", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_NeverMutatesPrintCopyTables()
    {
        var script = ReadableHtmlInteractions.Script;
        Assert.Contains("closest('.print-copy')", script, StringComparison.Ordinal);
        Assert.Contains(".print-copy", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_CapsColumnCheckboxesButKeepsAllColumns()
    {
        var script = ReadableHtmlInteractions.Script;
        Assert.Contains("256", script, StringComparison.Ordinal);
        Assert.Contains("drrGroups", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Controls_ExposeTrustedFixedIdsInBothLanguages()
    {
        var ja = ReadableHtmlInteractions.Controls(true);
        var en = ReadableHtmlInteractions.Controls(false);
        foreach (var html in new[] { ja, en })
        {
            foreach (var id in new[]
            {
                "id=\"drr-search\"", "id=\"drr-search-btn\"", "id=\"drr-reset-btn\"",
                "id=\"drr-print-all-btn\"", "id=\"drr-global-status\"",
            })
            {
                Assert.Contains(id, html, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
            Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", html, StringComparison.Ordinal);
            Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);
        }
        Assert.NotEqual(ja, en);
        Assert.Contains("検索", ja, StringComparison.Ordinal);
        Assert.Contains("Search", en, StringComparison.Ordinal);
        Assert.Equal(ja, ReadableHtmlInteractions.Controls(true));
    }

    [Fact]
    public void Css_HidesControlsAndResetsInteractiveFiltersForPrint()
    {
        var css = ReadableHtmlInteractions.Css;
        Assert.Contains("@media print", css, StringComparison.Ordinal);
        Assert.Contains(".drr-tools", css, StringComparison.Ordinal);
        Assert.Contains(".drr-global-toolbar", css, StringComparison.Ordinal);
        Assert.Contains("display: none !important", css, StringComparison.Ordinal);
        Assert.Contains("drr-sec-hidden", css, StringComparison.Ordinal);
        Assert.Contains("drr-row-hidden", css, StringComparison.Ordinal);
        Assert.Contains("drr-col-hidden", css, StringComparison.Ordinal);
        Assert.Contains("data-drr-print-target", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Helper_IsInternalStaticClassWithOnlyStaticMembers()
    {
        var type = typeof(ReadableHtmlInteractions);
        Assert.True(type.IsSealed && type.IsAbstract);
        Assert.False(type.IsPublic);
        Assert.DoesNotContain(
            type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            member => member is not ConstructorInfo);
        Assert.Null(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault());
    }
}
