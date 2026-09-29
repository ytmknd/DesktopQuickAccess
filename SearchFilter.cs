using System.Text;

namespace DesktopQuickAccess;

/// <summary>
/// 絞り込み検索用の文字列正規化。
/// 日本語のファイル名を素直に探せるように、比較前に
/// 「全角英数→半角」「半角カタカナ→全角」「カタカナ→ひらがな」「大文字→小文字」
/// を揃えてしまう。これにより「ｶｲｷﾞ」「カイギ」「かいぎ」がすべて同じ扱いになる。
///
/// InvariantGlobalization=true でビルドしているため CompareOptions.IgnoreKanaType 等は
/// 期待通りに動かない。カルチャに依存しない自前の変換にしている。
/// </summary>
internal static class SearchText
{
    // U+FF61〜U+FF9F(半角カタカナと半角の句読点)に対応する全角文字。
    private const string HalfWidthKatakana =
        "。「」、・ヲァィゥェォャュョッーアイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワン゛゜";

    // 濁点/半濁点は直前の文字に合成する(「ｶ」+「ﾞ」→「が」)。
    // 変換はカタカナ→ひらがなの後に行うため、ひらがなで表を持つ。
    private const string VoicedBase = "かきくけこさしすせそたちつてとはひふへほう";
    private const string VoicedComposed = "がぎぐげございずぜぞだぢづでどばびぶべぼゔ";
    private const string SemiVoicedBase = "はひふへほ";
    private const string SemiVoicedComposed = "ぱぴぷぺぽ";

    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var original in value)
        {
            var c = original;

            if (c is >= '！' and <= '～')
            {
                // 全角英数記号 → 半角
                c = (char)(c - 0xFEE0);
            }
            else if (c == '　')
            {
                // 全角スペース → 半角スペース(語区切りとして扱えるようにする)
                c = ' ';
            }
            else if (c is >= '｡' and <= 'ﾟ')
            {
                // 半角カタカナ → 全角カタカナ
                c = HalfWidthKatakana[c - '｡'];
            }

            if (c is >= 'ァ' and <= 'ヶ' or 'ヽ' or 'ヾ')
            {
                // カタカナ → ひらがな(繰り返し記号のヽヾも含む)
                c = (char)(c - 0x60);
            }

            // 単独の濁点/半濁点(合成済みでない文字列や NFD のファイル名で現れる)
            if (c is '゛' or '゙')
            {
                if (Compose(builder, VoicedBase, VoicedComposed))
                {
                    continue;
                }
            }
            else if (c is '゜' or '゚')
            {
                if (Compose(builder, SemiVoicedBase, SemiVoicedComposed))
                {
                    continue;
                }
            }

            builder.Append(c);
        }

        return builder.ToString().ToLowerInvariant();
    }

    private static bool Compose(StringBuilder builder, string baseChars, string composed)
    {
        if (builder.Length == 0)
        {
            return false;
        }

        var index = baseChars.IndexOf(builder[^1]);
        if (index < 0)
        {
            return false;
        }

        builder[^1] = composed[index];
        return true;
    }
}

/// <summary>
/// メニューを絞り込むための検索語。空白で区切った複数語は AND 条件になる。
/// </summary>
internal sealed class SearchQuery
{
    public const int NoMatch = -1;
    public const int PrefixMatch = 0;
    public const int ContainsMatch = 1;

    private readonly string[] _terms;

    private SearchQuery(string[] terms) => _terms = terms;

    /// <summary>入力が空(または空白のみ)なら null を返す = 絞り込みなし。</summary>
    public static SearchQuery? Create(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var terms = SearchText.Normalize(input)
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return terms.Length == 0 ? null : new SearchQuery(terms);
    }

    /// <summary>
    /// 一致したかどうかと、並び順に使う優先度を返す。
    /// 先頭一致(<see cref="PrefixMatch"/>)を部分一致より前に出すため数値が小さい。
    /// </summary>
    public int Match(string name)
    {
        var normalized = SearchText.Normalize(name);
        var prefix = false;

        foreach (var term in _terms)
        {
            var index = normalized.IndexOf(term, StringComparison.Ordinal);
            if (index < 0)
            {
                return NoMatch;
            }

            if (index == 0)
            {
                prefix = true;
            }
        }

        return prefix ? PrefixMatch : ContainsMatch;
    }
}
