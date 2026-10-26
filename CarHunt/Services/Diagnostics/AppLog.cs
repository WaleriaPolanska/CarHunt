using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CarHunt.Services.Diagnostics;

/// <summary>
/// A small local log, so a search that came back short can be explained after the fact
/// rather than guessed at. Until now every failure was swallowed silently and left no
/// trace at all.
///
/// It stays on the phone — nothing is sent anywhere — and it is capped, so it cannot
/// grow into a problem of its own.
/// </summary>
public static class AppLog
{
    private const int KeepLines = 300;
    private static readonly object Gate = new();
    private static readonly List<string> Recent = new();
    private static string? _file;

    public static IReadOnlyList<string> Lines
    {
        get { lock (Gate) return Recent.ToList(); }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:dd.MM HH:mm:ss}  {message}";

        lock (Gate)
        {
            Recent.Add(line);
            if (Recent.Count > KeepLines) Recent.RemoveRange(0, Recent.Count - KeepLines);

            try
            {
                _file ??= Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "carhunt.log");

                // Rewritten rather than appended: the file then holds exactly what is in
                // hand, capped, instead of growing for the life of the install.
                File.WriteAllLines(_file, Recent);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
