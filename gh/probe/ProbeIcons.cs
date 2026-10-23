using System;
using System.Linq;
using System.Reflection;

static class ProbeIcons
{
    public static void Run()
    {
        Console.WriteLine("--- AppearanceSettings static props ---");
        var t = typeof(Rhino.ApplicationSettings.AppearanceSettings);
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
            Console.WriteLine($"  PROP {p.Name} : {p.PropertyType.Name}");

        Console.WriteLine("--- ColorTheme enum ---");
        var et = t.Assembly.GetType("Rhino.ApplicationSettings.ColorTheme");
        Console.WriteLine("  exists: " + (et != null));
        if (et != null)
            foreach (var n in Enum.GetNames(et)) Console.WriteLine("    VAL " + n);

        Console.WriteLine("--- GH_Skin statics ---");
        var skin = typeof(Grasshopper.Kernel.GH_DocumentObject).Assembly.GetType("Grasshopper.GUI.GH_Skin");
        Console.WriteLine("  exists: " + (skin != null));
        if (skin != null)
        {
            foreach (var f in skin.GetFields(BindingFlags.Public | BindingFlags.Static))
                Console.WriteLine($"  FIELD {f.Name} : {f.FieldType.Name}");
        }
    }
}
