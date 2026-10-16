using Island.Core;

namespace Island.Core.Tests;

public sealed class DataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "island-migrate-" + Guid.NewGuid().ToString("N"));

    private string Old => Path.Combine(_root, "old");
    private string New => Path.Combine(_root, "new");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Write(string dir, string name, string content)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), content);
    }

    [Fact]
    public void The_users_choices_move_to_the_new_folder_and_the_old_one_is_left_alone()
    {
        Write(Old, "projects.json", "projects");
        Write(Old, "state.json", "state");
        Write(Old, "config.json", "config");

        var copied = IslandPaths.MigrateData(Old, New);

        Assert.Equal(3, copied);
        Assert.Equal("projects", File.ReadAllText(Path.Combine(New, "projects.json")));
        Assert.Equal("state", File.ReadAllText(Path.Combine(New, "state.json")));
        Assert.Equal("config", File.ReadAllText(Path.Combine(New, "config.json")));
        Assert.True(File.Exists(Path.Combine(Old, "projects.json"))); // a build still running from the old folder keeps working
    }

    [Fact]
    public void A_file_already_in_the_new_folder_is_never_overwritten()
    {
        Write(Old, "projects.json", "old choices");
        Write(New, "projects.json", "newer choices");

        var copied = IslandPaths.MigrateData(Old, New);

        Assert.Equal(0, copied);
        Assert.Equal("newer choices", File.ReadAllText(Path.Combine(New, "projects.json")));
    }

    [Fact]
    public void Running_it_twice_changes_nothing_the_second_time()
    {
        Write(Old, "projects.json", "projects");

        Assert.Equal(1, IslandPaths.MigrateData(Old, New));
        Assert.Equal(0, IslandPaths.MigrateData(Old, New));
    }

    [Fact]
    public void Nothing_happens_without_an_old_folder_and_only_known_files_are_copied()
    {
        Assert.Equal(0, IslandPaths.MigrateData(Old, New));
        Assert.False(Directory.Exists(New));

        Write(Old, "logs.txt", "may contain prompts");
        Write(Old, "projects.json", "projects");

        Assert.Equal(1, IslandPaths.MigrateData(Old, New));
        Assert.False(File.Exists(Path.Combine(New, "logs.txt")));
    }
}
