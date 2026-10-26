using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tartarus.App;

/// <summary>
/// What is running: the app's version and build time, and each model it loads, by a short hash of its file and when it
/// was trained (its .json's "trained", else the file's time). Two reports with the same line ran the same weights.
/// </summary>
public static class AppVersion
{
    public sealed record Model(string Name, string Path, string Hash, DateTime Trained);

    // Before Models: static fields are set in order.
    private static readonly (string Name, string File)[] Known =
    [
        ("cells", @"cells\cellnet.onnx"),
        ("arrows", @"cells\arrownet.onnx"),
        ("instruments", @"slots\instrument.onnx"),
        ("chromatic", @"slots\chromatic.onnx"),
        ("sandbox", @"sandbox\template.bin"),
        ("value", @"solver\value.bin"),
        ("weights", @"solver\weights.txt"),
    ];

    public static string Number { get; } = typeof(AppVersion).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    public static DateTime Built { get; } = File.GetLastWriteTime(Environment.ProcessPath ?? typeof(AppVersion).Assembly.Location);

    public static IReadOnlyList<Model> Models { get; } = FindModels();

    /// <summary>"0.5.0 (25.10 18:40) · cells 25.10 3fa1c2 · value 24.10 9b02e1": the app and the weights that matter most.</summary>
    public static string Short => $"{Number} ({Built:dd.MM HH:mm})"
        + string.Concat(Models.Where(m => m.Name is "cells" or "value").Select(m => $" · {m.Name} {m.Trained:dd.MM} {m.Hash}"));

    /// <summary>Every model, one per line.</summary>
    public static string Full => $"Tartarus Solver {Number}, built {Built:yyyy-MM-dd HH:mm}"
        + string.Concat(Models.Select(m => $"\n{m.Name}: {m.Hash}, {m.Trained:yyyy-MM-dd HH:mm} ({m.Path})"));

    private static List<Model> FindModels()
    {
        var models = new List<Model>();
        if (FindModelsDir() is not { } dir) return models;
        foreach (var (name, file) in Known)
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) continue;
            try
            {
                using var stream = File.OpenRead(path);
                string hash = Convert.ToHexStringLower(SHA256.HashData(stream))[..6];
                models.Add(new Model(name, @"models\" + file, hash, Trained(path) ?? File.GetLastWriteTime(path)));
            }
            catch (IOException) { }
        }
        return models;
    }

    private static DateTime? Trained(string modelPath)
    {
        string meta = Path.ChangeExtension(modelPath, ".json");
        if (!File.Exists(meta)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            return doc.RootElement.TryGetProperty("trained", out var t) && t.GetString() is { } s
                && DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    private static string? FindModelsDir() => AppFiles.Find("models", "cells") is { } cells ? Path.GetDirectoryName(cells) : null;
}
