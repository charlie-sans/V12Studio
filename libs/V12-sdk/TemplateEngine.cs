using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using V12.Core;
using V12.Core.UI;
using V12Studio.Core;

namespace V12.Sdk.Templating
{
    /// <summary>
    /// Generates a new V12 game project from a template.
    /// Copies a template directory tree and replaces placeholder tokens
    /// in .cs and .worldml files with provided values.
    /// </summary>
    public sealed class TemplateEngine
    {
        /// <summary>Placeholder patterns the engine recognises.</summary>
        private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
        {
            "{{PROJECT_NAME}}",
            "{{AUTHOR}}",
            "{{DATE}}",
            "{{NAMESPACE}}",
            "{{CLASS_NAME}}",
            "{{SCRIPT_NAME}}"
        };

        /// <summary>Template manifest describing a project template.</summary>
        public sealed class TemplateManifest
        {
            /// <summary>Template display name.</summary>
            public string Name { get; set; } = "Untitled";

            /// <summary>Template description.</summary>
            public string Description { get; set; } = "";

            /// <summary>Root directory inside the template containing files to process.</summary>
            public string Root { get; set; } = ".";

            /// <summary>Optional: relative path to a project config JSON (processed for placeholders).</summary>
            public string Config { get; set; } = null;
        }

        /// <summary>Available built-in templates.</summary>
        public static readonly TemplateManifest BlankGame = new()
        {
            Name = "Blank Game",
            Description = "Minimal V12 project with a starter scene and empty script template.",
            Root = "BlankGame"
        };

        /// <summary>Generate a new project from <paramref name="template"/> under <paramref name="targetDir"/>.</summary>
        /// <param name="template">Template manifest (one of the built-in entries).</param>
        /// <param name="targetDir">Directory to create the project in. Must not exist yet.</param>
        /// <param name="parameters">Placeholder values. Key must match <see cref="Placeholders"/>.</param>
        /// <returns>Full path to the generated project root.</returns>
        /// <exception cref="ArgumentException">If <paramref name="targetDir"/> already exists.</exception>
        /// <exception cref="IOException">If file system operations fail.</exception>
        public static string Generate(TemplateManifest template, string targetDir, Dictionary<string, string> parameters)
        {
            if (Directory.Exists(targetDir))
                throw new ArgumentException($"Project directory already exists: {targetDir}");

            // 1. Copy template root to target
            string templateRoot = FindTemplateRoot(template.Root);
            CopyDirectory(templateRoot, targetDir, false);

            // 2. Process placeholder replacement in supported files
            foreach (var file in Directory.GetFiles(targetDir, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (ext == ".cs" || ext == ".worldml" || ext == ".json")
                {
                    string content = File.ReadAllText(file);
                    bool changed = false;
                    foreach (var ph in Placeholders)
                    {
                        if (parameters.TryGetValue(ph, out var val))
                        {
                            content = content.Replace(ph, val);
                            changed = true;
                        }
                    }
                    if (changed)
                        File.WriteAllText(file, content);
                }
            }

            // 3. Process optional config file
            if (!string.IsNullOrWhiteSpace(template.Config))
            {
                string configPath = Path.Combine(targetDir, template.Config.TrimStart('./'));
                if (File.Exists(configPath))
                {
                    string configContent = File.ReadAllText(configPath);
                    foreach (var ph in Placeholders)
                    {
                        if (parameters.TryGetValue(ph, out var val))
                            configContent = configContent.Replace(ph, val);
                    }
                    File.WriteAllText(configPath, configContent);
                }
            }

            return Path.GetFullPath(targetDir);
        }

        /// <summary>Resolve the template root path relative to the engine's base location.</summary>
        private static string FindTemplateRoot(string relativePath)
        {
            // Templates are stored under libs/V12-sdk/Templates/
            // Derive the base directory from the caller's location.
            string codeBase = typeof(TemplateEngine).Assembly.Location;
            string asmDir = Path.GetDirectoryName(codeBase) ?? ".";
            return Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "libs", "V12-sdk", "Templates", relativePath));
        }

        /// <summary>Copy a directory tree.</summary>
        private static void CopyDirectory(string sourceDirName, string destDirName, bool copySubdirs)
        {
            DirectoryInfo di = new DirectoryInfo(sourceDirName);

            if (!di.Exists)
                return;

            DirectoryInfo[] diArr = di.GetDirectories();
            FileInfo[] fiArr = di.GetFiles();

            foreach (FileInfo fi in fiArr)
                fi.CopyTo(Path.Combine(destDirName, fi.Name), true);

            if (copySubdirs)
            {
                foreach (DirectoryInfo subDir in diArr)
                    CopyDirectory(subDir.FullName, Path.Combine(destDirName, subDir.Name), copySubdirs);
            }
        }
    }
}