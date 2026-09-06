using WebAppBookLibrary.Configuration;

namespace WebAppBookLibrary.Tests;

[Collection("Environment file loader")]
public sealed class EnvironmentFileLoaderTests
{
    [Fact]
    public void Load_prefers_dotenv_next_to_solution_over_nested_file()
    {
        var variableName = $"BOOK_LIBRARY_ENV_TEST_{Guid.NewGuid():N}";
        var root = Path.Combine(Path.GetTempPath(), $"book-library-{Guid.NewGuid():N}");
        var nestedDirectory = Path.Combine(root, "project", "bin");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(Path.Combine(root, "BookLibrary.sln"), string.Empty);
        File.WriteAllText(Path.Combine(root, ".env"), $"{variableName}=loaded-from-parent");
        File.WriteAllText(Path.Combine(nestedDirectory, ".env"), $"{variableName}=obsolete-value");

        try
        {
            Environment.SetEnvironmentVariable(variableName, null);

            EnvironmentFileLoader.Load(nestedDirectory);

            Assert.Equal("loaded-from-parent", Environment.GetEnvironmentVariable(variableName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
            Directory.Delete(root, recursive: true);
        }
    }
}

[CollectionDefinition("Environment file loader", DisableParallelization = true)]
public sealed class EnvironmentFileLoaderCollection;
