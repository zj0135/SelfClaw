using System.Text;
using System.Text.RegularExpressions;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Infrastructure.Extensions.Skills;

internal sealed partial class SkillTokenParser
{
    public IReadOnlyList<SkillToken> Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return VisibleTokenPattern().Matches(text)
            .Select(match =>
            {
                var id = match.Groups["id"].Value;
                return new SkillToken(
                    match.Value,
                    id,
                    id.Length <= 64 && ExecutableIdPattern().IsMatch(id),
                    match.Index,
                    match.Length);
            })
            .ToArray();
    }

    public static string RemoveTokens(string text, IReadOnlyList<SkillToken> tokens)
    {
        if (tokens.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var offset = 0;
        foreach (var token in tokens.OrderBy(token => token.Index))
        {
            builder.Append(text, offset, token.Index - offset);
            offset = token.Index + token.Length;
        }

        builder.Append(text, offset, text.Length - offset);
        return builder.ToString();
    }

    [GeneratedRegex(@"\[/(?<id>[^\]\r\n]{1,80})\]")]
    private static partial Regex VisibleTokenPattern();

    [GeneratedRegex(@"^[a-z0-9-]+(?:/[a-z0-9-]+)?$")]
    private static partial Regex ExecutableIdPattern();
}
