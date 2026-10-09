// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Fugu0141

namespace Meltype.Composition;

/// <summary>
/// IncrementalBoundaryLab v1.2 から抽出した最小の境界情報共有器。
/// コード形式の英字候補が始まる位置を、かな単位ベースの点数探索にも渡す。
/// このクラスは変換も確定も行わず、境界候補 (単位 index) だけを返す。
/// </summary>
internal static class StreamBoundaryHints
{
    private const int MaxTokenLength = 24;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "js", "jsx", "ts", "tsx", "json", "html", "css", "py", "rs", "md",
        "cs", "cpp", "com", "org", "net", "io", "dev"
    };

    // ローマ字としても読める開発用語は、語形の根拠として限定的に許可。
    // これ以外でも既存 Meltype の辞書で英語と判定できれば候補になる。
    private static readonly HashSet<string> CodeRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "github", "python", "rust", "react", "test",
        "index", "main", "app", "file", "package", "api", "www"
    };

    /// <summary>
    /// key: コード候補の開始単位、value: 終端単位 (exclusive)。
    /// 境界は確定扱いせず、Meltype の日本語・英語候補と競合させる。
    /// </summary>
    public static Dictionary<int, List<int>> FindCodeSpans(
        IReadOnlyList<CompositionUnit> units,
        Func<string, bool> knownEnglishWord,
        Func<string, bool> validRomaji)
    {
        var result = new Dictionary<int, List<int>>();
        for (var start = 0; start < units.Count; start++)
        {
            if (units[start].Raw.Length == 0 ||
                !char.IsAsciiLetter(units[start].Raw[0])) continue;

            var raw = "";
            for (var end = start + 1; end <= units.Count; end++)
            {
                raw += units[end - 1].Raw;
                if (raw.Length > MaxTokenLength) break;
                var dot = raw.IndexOf('.');
                if (dot < 2 || dot == raw.Length - 1 ||
                    raw.LastIndexOf('.') != dot) continue;

                var stem = raw[..dot];
                var extension = raw[(dot + 1)..];
                if (!stem.All(char.IsAsciiLetterOrDigit) ||
                    !extension.All(char.IsAsciiLetter) ||
                    !Extensions.Contains(extension)) continue;

                // 適当な日本語の連続ローマ字を .js まで巻き込まない。
                // 「このようにnode.js」で konoyouninode.js を一語と
                // 認識することを避け、node.js の開始位置だけを支持する。
                if (!CodeRoots.Contains(stem) &&
                    !knownEnglishWord(stem) &&
                    validRomaji(stem)) continue;

                if (!result.TryGetValue(start, out var ends))
                    result[start] = ends = [];
                ends.Add(end);
            }
        }
        return result;
    }
}
