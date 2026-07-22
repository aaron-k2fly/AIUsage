using AIUsage.Scanner;

namespace AIUsage.Tests;

public class SessionAggregatorTests
{
    private const string FilePath = "/transcripts/test.jsonl";

    private static Dictionary<string, SessionAggregate> Aggregate(string[] lines, params string[] allowlist)
    {
        var inferrer = new TicketKeyInferrer([.. allowlist]);
        return new SessionAggregator(inferrer).Aggregate(lines, FilePath);
    }

    [Fact]
    public void Aggregate_accumulates_tokens_tools_model_and_timestamps()
    {
        var lines = new[]
        {
            """{"sessionId":"s1","type":"assistant","timestamp":"2026-07-01T10:00:00Z","cwd":"/repo","gitBranch":"feature/ABC-1","version":"1.2.3","message":{"model":"claude-opus-4-8","usage":{"input_tokens":10,"output_tokens":5,"cache_creation_input_tokens":2,"cache_read_input_tokens":3},"content":[{"type":"tool_use","name":"Edit"},{"type":"tool_use","name":"Write"},{"type":"tool_use","name":"Read"},{"type":"tool_use","name":"Bash"},{"type":"tool_use","name":"TodoWrite"}]}}""",
            """{"sessionId":"s1","type":"user","timestamp":"2026-07-01T10:05:00Z","message":{"content":"please work on ABC-1"}}""",
            """{"sessionId":"s1","type":"assistant","timestamp":"2026-07-01T09:59:00Z","message":{"usage":{"input_tokens":1,"output_tokens":1}}}""",
        };

        var agg = Aggregate(lines, "ABC")["s1"];

        Assert.Equal(11, agg.InputTokens);
        Assert.Equal(6, agg.OutputTokens);
        Assert.Equal(2, agg.CacheCreationTokens);
        Assert.Equal(3, agg.CacheReadTokens);
        Assert.Equal(1, agg.EditCount);
        Assert.Equal(1, agg.WriteCount);
        Assert.Equal(1, agg.ReadCount);
        Assert.Equal(1, agg.BashCount);
        Assert.Equal(1, agg.OtherToolCount);   // TodoWrite falls into "other"
        Assert.Equal(1, agg.UserMessageCount);
        Assert.Equal("claude-opus-4-8", agg.Model);
        Assert.Equal("2026-07-01T09:59:00Z", agg.StartedAt); // earliest
        Assert.Equal("2026-07-01T10:05:00Z", agg.EndedAt);   // latest
        Assert.Equal("1.2.3", agg.CcVersion);
        Assert.Equal("/repo", agg.ProjectDir);
    }

    [Fact]
    public void Aggregate_groups_lines_by_session_id()
    {
        var lines = new[]
        {
            """{"sessionId":"a","type":"assistant","message":{"usage":{"input_tokens":5,"output_tokens":0}}}""",
            """{"sessionId":"b","type":"assistant","message":{"usage":{"input_tokens":7,"output_tokens":0}}}""",
        };

        var sessions = Aggregate(lines);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(5, sessions["a"].InputTokens);
        Assert.Equal(7, sessions["b"].InputTokens);
    }

    [Fact]
    public void Aggregate_skips_sidechain_lines()
    {
        var lines = new[]
        {
            """{"sessionId":"sc","type":"assistant","isSidechain":true,"message":{"usage":{"input_tokens":100,"output_tokens":100}}}""",
        };

        Assert.Empty(Aggregate(lines));
    }

    [Fact]
    public void Aggregate_skips_malformed_and_blank_lines_without_throwing()
    {
        var lines = new[]
        {
            """{"sessionId":"s1","type":"assistant","message":{"usage":{"input_tokens":4,"output_tokens":0}}}""",
            "{ this is not valid json",
            "",
            "   ",
            """{"sessionId":"s1","type":"assistant","message":{"usage":{"input_tokens":6,"output_tokens":0}}}""",
        };

        var agg = Aggregate(lines)["s1"];
        Assert.Equal(10, agg.InputTokens); // both valid lines counted; junk skipped
    }

    [Fact]
    public void Aggregate_lets_a_custom_title_override_an_ai_title()
    {
        var lines = new[]
        {
            """{"sessionId":"t1","type":"ai-title","aiTitle":"AI Generated"}""",
            """{"sessionId":"t1","type":"custom-title","customTitle":"My Title"}""",
        };

        var agg = Aggregate(lines)["t1"];
        Assert.Equal("My Title", agg.Title);
        Assert.True(agg.TitleIsCustom);
    }

    [Fact]
    public void Aggregate_never_lets_an_ai_title_overwrite_a_custom_title()
    {
        var lines = new[]
        {
            """{"sessionId":"t2","type":"custom-title","customTitle":"Mine"}""",
            """{"sessionId":"t2","type":"ai-title","aiTitle":"Auto"}""",
        };

        var agg = Aggregate(lines)["t2"];
        Assert.Equal("Mine", agg.Title);
        Assert.True(agg.TitleIsCustom);
    }

    [Fact]
    public void Aggregate_prefers_branch_over_cwd_and_prompt_for_the_same_key()
    {
        var lines = new[]
        {
            """{"sessionId":"s1","type":"user","cwd":"/work/ABC-1","gitBranch":"feature/ABC-1","message":{"content":"work on ABC-1"}}""",
        };

        var agg = Aggregate(lines, "ABC")["s1"];
        Assert.Equal("branch", agg.TicketKeys["ABC-1"]);
    }

    [Fact]
    public void Aggregate_falls_back_to_cwd_when_the_branch_is_not_real()
    {
        var lines = new[]
        {
            """{"sessionId":"s1","type":"user","cwd":"/work/ABC-2","gitBranch":"main","message":{"content":"hello"}}""",
        };

        var agg = Aggregate(lines, "ABC")["s1"];
        Assert.Equal("cwd", agg.TicketKeys["ABC-2"]);
    }

    [Fact]
    public void Aggregate_infers_prompt_text_keys_when_no_branch_or_cwd_key()
    {
        var lines = new[]
        {
            """{"sessionId":"s1","type":"user","message":{"content":"do ABC-3 please"}}""",
        };

        var agg = Aggregate(lines, "ABC")["s1"];
        Assert.Equal("prompt_text", agg.TicketKeys["ABC-3"]);
    }

    [Fact]
    public void Aggregate_ignores_array_user_content_for_keys_and_message_count()
    {
        // Array content = tool_result noise; it must not be mined for keys or counted as a prompt.
        var lines = new[]
        {
            """{"sessionId":"s1","type":"user","message":{"content":[{"type":"tool_result","content":"ABC-9 in output"}]}}""",
        };

        var agg = Aggregate(lines, "ABC")["s1"];
        Assert.Equal(0, agg.UserMessageCount);
        Assert.Empty(agg.TicketKeys);
    }

    [Theory]
    [InlineData("claude-haiku-4-5-20251001", 200_000)]
    [InlineData("HAIKU", 200_000)]           // case-insensitive
    [InlineData("claude-opus-4-8", 1_000_000)]
    [InlineData("sonnet", 1_000_000)]
    [InlineData("fable", 1_000_000)]
    [InlineData("", 1_000_000)]
    [InlineData(null, 1_000_000)]
    public void ContextWindow_is_200k_only_for_haiku(string? model, long expected)
    {
        Assert.Equal(expected, SessionAggregator.ContextWindow(model));
    }
}
