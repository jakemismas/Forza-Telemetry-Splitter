using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Resources;
using ForzaTelemetrySplitter.Resources;

// Verifies the localization layer against the real Strings accessor + satellite assemblies:
//  - every key in the neutral resource (Strings.resx) is present, non-empty, in every locale
//  - supported cultures also return translated (non-English, non-empty) text for spot-checked keys
//  - an unsupported culture falls back to English
//  - placeholder-formatting helpers produce the expected substitutions per culture

int failures = 0;
Console.WriteLine("=== Forza Telemetry Splitter — localization verification ===\n");

string English(string key)
{
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    return Strings.Get(key);
}

void Check(string culture, bool mustDifferFromEnglish)
{
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
    string noData = Strings.Overlay_NoData;
    string start = Strings.Tray_StartSplitting;
    string enNoData = English("Overlay_NoData");

    // restore the test culture (English() flipped it)
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
    noData = Strings.Overlay_NoData;
    start = Strings.Tray_StartSplitting;

    bool nonEmpty = !string.IsNullOrWhiteSpace(noData) && !string.IsNullOrWhiteSpace(start);
    bool differs = !mustDifferFromEnglish || noData != enNoData;
    bool ok = nonEmpty && differs;
    Console.WriteLine($"  {culture,-3}: Overlay_NoData='{noData}'  Tray_StartSplitting='{start}'  -> {(ok ? "PASS" : "FAIL")}");
    if (!ok) failures++;
}

Console.WriteLine("[Test 1] Every key in Strings.resx is present and non-empty in every locale");
{
    // Walk each satellite resource set directly (tryParents:false) so a key that is only
    // reachable by falling back to English is caught as missing, not silently passed.
    var rm = new ResourceManager("ForzaTelemetrySplitter.Resources.Strings", typeof(Strings).Assembly);
    var neutralSet = rm.GetResourceSet(CultureInfo.InvariantCulture, true, false)
        ?? throw new InvalidOperationException("Could not load the neutral Strings.resx resource set.");
    var canonicalKeys = neutralSet.Cast<DictionaryEntry>()
        .Select(e => (string)e.Key)
        .OrderBy(k => k, StringComparer.Ordinal)
        .ToList();
    Console.WriteLine($"  canonical key count: {canonicalKeys.Count}");

    foreach (var culture in new[] { "ja", "fr", "de", "es" })
    {
        var set = rm.GetResourceSet(CultureInfo.GetCultureInfo(culture), true, false);
        var missing = new List<string>();
        var empty = new List<string>();
        foreach (var key in canonicalKeys)
        {
            string? value = set?.GetString(key);
            if (value is null) missing.Add(key);
            else if (string.IsNullOrWhiteSpace(value)) empty.Add(key);
        }
        bool ok = missing.Count == 0 && empty.Count == 0;
        int present = canonicalKeys.Count - missing.Count - empty.Count;
        Console.WriteLine($"  {culture,-3}: {present}/{canonicalKeys.Count} keys present and non-empty -> {(ok ? "PASS" : "FAIL")}");
        if (missing.Count > 0) Console.WriteLine($"       missing ({missing.Count}): {string.Join(", ", missing)}");
        if (empty.Count > 0) Console.WriteLine($"       empty ({empty.Count}): {string.Join(", ", empty)}");
        if (!ok) failures++;
    }
}

Console.WriteLine("\n[Test 2] Supported cultures return non-empty, translated text");
Check("en", mustDifferFromEnglish: false);
Check("ja", mustDifferFromEnglish: true);
Check("fr", mustDifferFromEnglish: true);
Check("de", mustDifferFromEnglish: true);
Check("es", mustDifferFromEnglish: true);

Console.WriteLine("\n[Test 3] New v0.6 strings load and differ per culture");
{
    bool ok = true;
    foreach (var c in new[] { "ja", "fr", "de", "es" })
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(c);
        string fwd = Strings.Dot_Forwarding;
        string tab = Strings.Tab_Activity;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        bool differs = fwd != Strings.Dot_Forwarding && tab != Strings.Tab_Activity
                       && !string.IsNullOrWhiteSpace(fwd);
        if (!differs) { ok = false; Console.WriteLine($"  {c}: Dot_Forwarding/Tab_Activity not localized"); }
    }
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}");
    if (!ok) failures++;
}

Console.WriteLine("\n[Test 4] Unsupported culture (it) falls back to English");
{
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("it");
    string itNoData = Strings.Overlay_NoData;
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    string enNoData = Strings.Overlay_NoData;
    bool ok = itNoData == enNoData && !string.IsNullOrWhiteSpace(itNoData);
    Console.WriteLine($"  it -> '{itNoData}' (expect English '{enNoData}') -> {(ok ? "PASS" : "FAIL")}");
    if (!ok) failures++;
}

Console.WriteLine("\n[Test 5] Placeholder formatting works per culture");
{
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja");
    string s = Strings.Status_Connected("Forza Horizon", 60, Strings.Status_RaceOn, "Gear 4   112 mph");
    // must contain the substituted values and no stray "{0}" tokens
    bool ok = s.Contains("Forza Horizon") && s.Contains("60") && !s.Contains("{0}") && !s.Contains("{1}");
    Console.WriteLine($"  ja Status_Connected -> '{s}' -> {(ok ? "PASS" : "FAIL")}");
    if (!ok) failures++;

    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");
    string e = Strings.Error_PortInUse(44405);
    bool eok = e.Contains("44405") && !e.Contains("{0}");
    Console.WriteLine($"  de Error_PortInUse contains port, no stray token -> {(eok ? "PASS" : "FAIL")}");
    if (!eok) failures++;
}

Console.WriteLine("\n[Test 6] StartupRegistration enable/disable (HKCU Run)");
{
    bool wasEnabled = ForzaTelemetrySplitter.Config.StartupRegistration.IsEnabled();
    try
    {
        ForzaTelemetrySplitter.Config.StartupRegistration.Enable();
        bool nowOn = ForzaTelemetrySplitter.Config.StartupRegistration.IsEnabled();
        ForzaTelemetrySplitter.Config.StartupRegistration.Disable();
        bool nowOff = ForzaTelemetrySplitter.Config.StartupRegistration.IsEnabled();
        bool ok = nowOn && !nowOff;
        Console.WriteLine($"  Enable->{nowOn}, Disable->{!nowOff} -> {(ok ? "PASS" : "FAIL")}");
        if (!ok) failures++;
    }
    finally
    {
        // Restore prior state so the test leaves no trace.
        if (wasEnabled) ForzaTelemetrySplitter.Config.StartupRegistration.Enable();
        else ForzaTelemetrySplitter.Config.StartupRegistration.Disable();
    }
}

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("OVERALL: PASS — all cultures load, fallback works, placeholders substitute.");
    return 0;
}
Console.WriteLine($"OVERALL: FAIL — {failures} check(s) failed.");
return 1;
