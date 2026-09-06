using DotNetEnv;

namespace WebAppBookLibrary.Configuration;

public static class EnvironmentFileLoader
{
    public static void Load(string startDirectory)
    {
        var currentDirectory = new DirectoryInfo(startDirectory);
        string? nearestEnvironmentFile = null;

        while (currentDirectory is not null)
        {
            var environmentFile = Path.Combine(currentDirectory.FullName, ".env");

            if (File.Exists(environmentFile))
            {
                nearestEnvironmentFile ??= environmentFile;

                if (currentDirectory.EnumerateFiles("*.sln", SearchOption.TopDirectoryOnly).Any())
                {
                    Env.Load(environmentFile);
                    return;
                }
            }

            currentDirectory = currentDirectory.Parent;
        }

        if (nearestEnvironmentFile is not null)
        {
            Env.Load(nearestEnvironmentFile);
        }
    }
}
