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

    private string Zip(params (string Name, string Text)[] files)
    {
        string zip = Path.Combine(_root, "update.zip");
        using var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (name, text) in files)
        {
            using var w = new StreamWriter(archive.CreateEntry(name).Open());
            w.Write(text);
        }
        return zip;
    }

    [Fact]
    public void A_runtime_file_that_can_t_be_written_is_left_out_of_an_unpacked_update_and_any_other_fails_it()
    {
        string zip = Zip(("TartarusSolver/app.dll", "new app"), ("TartarusSolver/msvcp140.dll", "runtime"), ("TartarusSolver/ru/words.dll", "new words"));
        string to = Path.Combine(_root, "unpacked");
        // Neither can be written: a folder stands in the file's place.
        Directory.CreateDirectory(Path.Combine(to, "TartarusSolver", "msvcp140.dll"));
        var missed = new List<string>();
        FolderCopy.Unzip(zip, to, FolderCopy.IsVcRuntime, (name, _) => missed.Add(name));
        Assert.Equal(["TartarusSolver/msvcp140.dll"], missed);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(to, "TartarusSolver", "app.dll")));
        Assert.Equal("new words", File.ReadAllText(Path.Combine(to, "TartarusSolver", "ru", "words.dll")));

        string other = Path.Combine(_root, "unpacked2");
        Directory.CreateDirectory(Path.Combine(other, "TartarusSolver", "app.dll"));
        Assert.ThrowsAny<Exception>(() => FolderCopy.Unzip(zip, other, FolderCopy.IsVcRuntime, (_, _) => { }));
    }

    [Fact]
    public void Nothing_is_unpacked_outside_the_folder()
    {
        string zip = Zip(("../outside.txt", "x"));
        Assert.Throws<IOException>(() => FolderCopy.Unzip(zip, Path.Combine(_root, "unpacked"), _ => false, (_, _) => { }));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
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
