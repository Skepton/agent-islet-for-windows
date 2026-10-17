using Island.Core;

namespace Island.Core.Tests;

public class IslandPipeNameTests
{
    [Fact]
    public void The_pipe_uses_the_new_product_prefix_and_the_old_one_is_kept_for_hooks_installed_before_the_rename()
    {
        var saved = Environment.GetEnvironmentVariable(IslandPipe.OverrideEnvVar);
        Environment.SetEnvironmentVariable(IslandPipe.OverrideEnvVar, null);
        try
        {
            var sid = IslandPipe.CurrentUserSid();

            Assert.Equal($"agent-islet-{sid}", IslandPipe.ResolveName());
            Assert.Equal($"claude-code-island-{sid}", IslandPipe.ResolveLegacyName());
        }
        finally
        {
            Environment.SetEnvironmentVariable(IslandPipe.OverrideEnvVar, saved);
        }
    }

    [Fact]
    public void An_overridden_name_means_one_pipe_only()
    {
        var saved = Environment.GetEnvironmentVariable(IslandPipe.OverrideEnvVar);
        Environment.SetEnvironmentVariable(IslandPipe.OverrideEnvVar, "my-test-pipe");
        try
        {
            Assert.Equal("my-test-pipe", IslandPipe.ResolveName());
            Assert.Null(IslandPipe.ResolveLegacyName());
        }
        finally
        {
            Environment.SetEnvironmentVariable(IslandPipe.OverrideEnvVar, saved);
        }
    }
}
