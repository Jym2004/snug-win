using System;
using System.Collections.Generic;
using System.Linq;

namespace SnugWin.Core.Session;

public enum UuElementKind
{
    Text,
    Table,
    Row,
    Other
}

public sealed record UuElement(UuElementKind Kind, string? Text, IReadOnlyList<UuElement> Children)
{
    public static UuElement TextNode(string text) => new(UuElementKind.Text, text, Array.Empty<UuElement>());
    public static UuElement TableNode(IReadOnlyList<UuElement> children) => new(UuElementKind.Table, null, children);
    public static UuElement RowNode(IReadOnlyList<UuElement> children) => new(UuElementKind.Row, null, children);
    public static UuElement OtherNode(IReadOnlyList<UuElement> children) => new(UuElementKind.Other, null, children);

    internal IEnumerable<UuElement> Rows()
    {
        if (Kind == UuElementKind.Row)
            yield return this;
        foreach (var child in Children)
        {
            foreach (var r in child.Rows())
                yield return r;
        }
    }

    internal IEnumerable<string> Texts()
    {
        if (Kind == UuElementKind.Text && !string.IsNullOrEmpty(Text))
            yield return Text;
        foreach (var child in Children)
        {
            foreach (var t in child.Texts())
                yield return t;
        }
    }
}

/// <summary>
/// Port of Snug's ControllerTable.swift:
/// Only recognizes remote-control rows inside tables paired directly under the "本机被控中" header,
/// and only when a row contains the "远程控制" marker. Prevents settings/UI text from triggering false sessions.
/// </summary>
public static class ControllerTableParser
{
    public const string PageTitle = "本机被控中";
    public const string ControlMark = "远程控制";
    public const string DurationPrefix = "连接时长";

    public static ControllerTable ParseWindows(IEnumerable<UuElement> windows)
    {
        var rows = windows
            .SelectMany(LocalTables)
            .SelectMany(t => t.Rows())
            .Select(r => (IReadOnlyList<string>)r.Texts().ToList());
        return ParseRows(rows);
    }

    public static ControllerTable ParseRows(IEnumerable<IReadOnlyList<string>> rows)
    {
        var names = new List<string>();
        foreach (var texts in rows)
        {
            if (!texts.Contains(ControlMark, StringComparer.Ordinal))
                continue;

            string? name = texts.FirstOrDefault(t =>
                !string.IsNullOrWhiteSpace(t)
                && !string.Equals(t, ControlMark, StringComparison.Ordinal)
                && !t.StartsWith(DurationPrefix, StringComparison.Ordinal));

            if (!string.IsNullOrEmpty(name) && !names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }
        return names.Count == 0 ? ControllerTable.Unrecognized : ControllerTable.FromNames(names.ToArray());
    }

    private static IEnumerable<UuElement> LocalTables(UuElement element)
    {
        bool hasHeader = element.Children.Any(c => c.Kind == UuElementKind.Text && string.Equals(c.Text, PageTitle, StringComparison.Ordinal));
        if (hasHeader)
        {
            foreach (var child in element.Children)
            {
                if (child.Kind == UuElementKind.Table)
                    yield return child;
            }
        }
        foreach (var child in element.Children)
        {
            foreach (var nested in LocalTables(child))
                yield return nested;
        }
    }
}
