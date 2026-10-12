using System.Text.Json;
using Island.Core;

namespace Island.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void Hook_frame_is_one_compact_line_and_round_trips()
    {
        var pretty = """
            {
              "event": "finished",
              "session": "s1"
            }
            """;
        using var doc = JsonDocument.Parse(pretty);

        var line = Frames.EncodeHook("abc", "test-agent", 1700000000000, 42, false, doc.RootElement);
        var frame = Frames.DecodeHook(line);

        Assert.DoesNotContain('\n', line);
        Assert.Equal("abc", frame.Id);
        Assert.Equal("test-agent", frame.Agent);
        Assert.Equal(1700000000000, frame.ReceivedAtUnixMs);
        Assert.Equal(42, frame.ClientPid);
        Assert.False(frame.Wait);
        Assert.Equal("{\"event\":\"finished\",\"session\":\"s1\"}", frame.PayloadJson);
    }

    [Fact]
    public void Client_app_travels_in_the_frame_and_is_optional()
    {
        using var doc = JsonDocument.Parse("""{"event":"x"}""");

        var with = Frames.DecodeHook(Frames.EncodeHook("a", "test-agent", 1, 2, false, doc.RootElement, "VS Code"));
        var without = Frames.DecodeHook(Frames.EncodeHook("a", "test-agent", 1, 2, false, doc.RootElement));

        Assert.Equal("VS Code", with.ClientApp);
        Assert.Null(without.ClientApp);
    }

    [Fact]
    public void Frame_with_another_protocol_version_is_rejected()
    {
        var line = "{\"v\":1,\"id\":\"x\",\"kind\":\"hook\",\"agent\":\"test-agent\",\"payload\":{}}";

        Assert.Throws<InvalidDataException>(() => Frames.DecodeHook(line));
    }

    [Fact]
    public void Frame_without_an_agent_is_rejected()
    {
        var line = "{\"v\":2,\"id\":\"x\",\"kind\":\"hook\",\"payload\":{}}";

        Assert.Throws<InvalidDataException>(() => Frames.DecodeHook(line));
    }

    [Fact]
    public void Null_output_round_trips_as_null()
    {
        var (id, output) = Frames.DecodeResponse(Frames.EncodeResponse("id-1", null));

        Assert.Equal("id-1", id);
        Assert.Null(output);
    }

    [Fact]
    public void Output_round_trips_verbatim_on_one_line()
    {
        var text = "{\"say\":\"no\"}\nsecond line <&> ü";

        var line = Frames.EncodeResponse("id-2", text);
        var (id, output) = Frames.DecodeResponse(line);

        Assert.DoesNotContain('\n', line);
        Assert.Equal("id-2", id);
        Assert.Equal(text, output);
    }

    [Fact]
    public void Long_text_is_truncated_with_an_ellipsis()
    {
        var result = TextUtil.Truncate(new string('a', 300), 10);

        Assert.Equal(10, result.Length);
        Assert.EndsWith("…", result);
    }
}
