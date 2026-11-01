using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Git;

namespace SelfClaw.Tests.Infrastructure.Git;

public sealed class GitStatusPorcelainParserTests
{
    private const string Hash = "4cb29ea38f70d7c61b2a3a25b02e3bdf44905402";

    [Fact]
    public void Ordinary_records_split_into_index_and_worktree_entries_by_xy()
    {
        var output =
            $"1 MM N... 100644 100644 100644 {Hash} {Hash} a.txt\0" +
            $"1 M. N... 100644 100644 100644 {Hash} {Hash} staged only.txt\0" +
            $"1 .D N... 100644 100644 100644 {Hash} {Hash} gone.txt\0";

        var entries = GitStatusPorcelainParser.ParseStatus(output, string.Empty);

        entries.Should().BeEquivalentTo(new[]
        {
            new GitStatusPorcelainParser.GitStatusEntry(GitChangeScope.Staged, "a.txt", null, GitChangeKind.Modified),
            new GitStatusPorcelainParser.GitStatusEntry(GitChangeScope.Unstaged, "a.txt", null, GitChangeKind.Modified),
            new GitStatusPorcelainParser.GitStatusEntry(GitChangeScope.Staged, "staged only.txt", null, GitChangeKind.Modified),
            new GitStatusPorcelainParser.GitStatusEntry(GitChangeScope.Unstaged, "gone.txt", null, GitChangeKind.Deleted)
        });
    }

    [Fact]
    public void Rename_records_consume_the_following_original_path_field()
    {
        // The original path is a separate NUL field. It deliberately looks like an untracked record so a
        // parser that forgets to consume it would surface a bogus entry.
        var output =
            $"2 RM N... 100644 100644 100644 {Hash} {Hash} R100 new.txt\0? decoy.txt\0" +
            "? fresh.txt\0";

        var entries = GitStatusPorcelainParser.ParseStatus(output, string.Empty);

        entries.Should().BeEquivalentTo(new[]
        {
            new GitStatusPorcelainParser.GitStatusEntry(GitChangeScope.Staged, "new.txt", "? decoy.txt", GitChangeKind.Renamed),
            new GitStatusPorcelainParser.GitStatusEntry(GitChangeScope.Unstaged, "new.txt", null, GitChangeKind.Modified),
            new GitStatusPorcelainParser.GitStatusEntry(GitChangeScope.Unstaged, "fresh.txt", null, GitChangeKind.Untracked)
        });
    }

    [Fact]
    public void Unmerged_records_appear_in_both_scopes_as_conflicts()
    {
        var output = $"u UU N... 100644 100644 100644 100644 {Hash} {Hash} {Hash} clash.txt\0";

        var entries = GitStatusPorcelainParser.ParseStatus(output, string.Empty);

        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(entry => entry.Path == "clash.txt" && entry.Kind == GitChangeKind.Conflicted && entry.OldPath == null);
        entries.Select(entry => entry.Scope).Should().BeEquivalentTo([GitChangeScope.Unstaged, GitChangeScope.Staged]);
    }

    [Fact]
    public void Prefix_is_stripped_and_paths_outside_the_workspace_subtree_are_dropped()
    {
        var output =
            $"1 M. N... 100644 100644 100644 {Hash} {Hash} dir/inside.txt\0" +
            $"1 .M N... 100644 100644 100644 {Hash} {Hash} outside.txt\0" +
            $"2 R. N... 100644 100644 100644 {Hash} {Hash} R100 other/new.txt\0other/old.txt\0" +
            "? dir/nested/fresh.txt\0";

        var entries = GitStatusPorcelainParser.ParseStatus(output, "dir/");

        entries.Should().BeEquivalentTo(new[]
        {
            new GitStatusPorcelainParser.GitStatusEntry(GitChangeScope.Staged, "inside.txt", null, GitChangeKind.Modified),
            new GitStatusPorcelainParser.GitStatusEntry(GitChangeScope.Unstaged, "nested/fresh.txt", null, GitChangeKind.Untracked)
        });
    }

    [Fact]
    public void Malformed_and_empty_tokens_are_ignored()
    {
        var output = "\0\01 MM\0# branch.head main\0? \0";

        GitStatusPorcelainParser.ParseStatus(output, string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Numstat_rows_map_paths_to_counts_including_rename_and_binary_rows()
    {
        var output =
            "1\t0\ta.txt\0" +
            "0\t0\t\0old.txt\0new.txt\0" +
            "-\t-\tblob.dat\0" +
            "3\t2\tdir/with space.txt\0";

        var counts = GitStatusPorcelainParser.ParseNumstat(output);

        counts.Should().HaveCount(4);
        counts["a.txt"].Should().Be(((int?)1, (int?)0));
        counts["new.txt"].Should().Be(((int?)0, (int?)0));
        counts["blob.dat"].Should().Be(((int?)null, (int?)null));
        counts["dir/with space.txt"].Should().Be(((int?)3, (int?)2));
        counts.Should().NotContainKey("old.txt");
    }
}
