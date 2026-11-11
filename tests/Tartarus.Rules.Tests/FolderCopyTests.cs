using Tartarus.Vision;

namespace Tartarus.Rules.Tests;

public class FolderCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tartarus-copy-" + Guid.NewGuid().ToString("N"));
    private string From => Path.Combine(_root, "new");
    private string To => Path.Combine(_root, "install");

    public FolderCopyTests()
    {
        Directory.CreateDirectory(Path.Combine(From, "ru"));
        Directory.CreateDirectory(To);
        File.WriteAllText(Path.Combine(From, "app.dll"), "new app");
        File.WriteAllText(Path.Combine(From, "ru", "words.dll"), "new words");
        File.WriteAllText(Path.Combine(To, "app.dll"), "old app");
        File.WriteAllText(Path.Combine(To, "mine.txt"), "the player's");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Every_file_takes_its_place_and_the_target_s_own_stay()
    {
        FolderCopy.Over(From, To, tries: 1, waitMs: 0);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(To, "app.dll")));
        Assert.Equal("new words", File.ReadAllText(Path.Combine(To, "ru", "words.dll")));
        Assert.Equal("the player's", File.ReadAllText(Path.Combine(To, "mine.txt")));
        Assert.Empty(Directory.EnumerateFiles(To, "*" + FolderCopy.Staged, SearchOption.AllDirectories));
    }

    [Fact]
    public void A_copy_that_fails_leaves_the_target_as_it_was()
    {
        // The second file can't be copied: a folder stands where its copy would go.
        Directory.CreateDirectory(Path.Combine(To, "ru", "words.dll" + FolderCopy.Staged));
        Assert.ThrowsAny<Exception>(() => FolderCopy.Over(From, To, tries: 2, waitMs: 0));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(To, "app.dll")));
        Assert.False(File.Exists(Path.Combine(To, "app.dll" + FolderCopy.Staged)));
    }
}
