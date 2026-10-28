using System;
using Microsoft.Win32;

namespace LyricPop;

/// <summary>Mulai bersama Windows lewat registry HKCU\...\Run (tanpa hak admin).</summary>
public static class Autostart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LyricPop";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            return k?.GetValue(ValueName) != null;
        }
        catch (Exception) { return false; }
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath, true);
            if (k == null) return;
            if (on) k.SetValue(ValueName, "\"" + Environment.ProcessPath + "\"");
            else k.DeleteValue(ValueName, false);
        }
        catch (Exception) { /* abaikan */ }
    }
}
