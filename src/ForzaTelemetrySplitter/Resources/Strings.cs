using System.Globalization;
using System.Resources;

namespace ForzaTelemetrySplitter.Resources;

/// <summary>
/// Strongly-typed access to the localized UI strings (Strings.resx + Strings.&lt;culture&gt;.resx).
/// Backed by a ResourceManager keyed off the embedded resource base name; the value returned
/// follows <see cref="CultureInfo.CurrentUICulture"/>, which Program sets at startup.
///
/// Written by hand (rather than the ResX designer generator) so it works reliably regardless of
/// generator settings and is easy to audit.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Rm =
        new("ForzaTelemetrySplitter.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>Look up a key for the current UI culture. Returns the key itself if missing
    /// (visible-but-safe, never throws).</summary>
    public static string Get(string key) => Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>Look up and format with arguments.</summary>
    public static string Format(string key, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    // Tray menu
    public static string Tray_Open => Get(nameof(Tray_Open));
    public static string Tray_StartSplitting => Get(nameof(Tray_StartSplitting));
    public static string Tray_StopSplitting => Get(nameof(Tray_StopSplitting));
    public static string Tray_ShowOverlay => Get(nameof(Tray_ShowOverlay));
    public static string Tray_SetupGuide => Get(nameof(Tray_SetupGuide));
    public static string Tray_CheckUpdates => Get(nameof(Tray_CheckUpdates));
    public static string Tray_Exit => Get(nameof(Tray_Exit));
    public static string Tray_GameDetected(string game) => Format(nameof(Tray_GameDetected), game);
    public static string Tray_GameClosed => Get(nameof(Tray_GameClosed));

    // Main window
    public static string Main_Title => Get(nameof(Main_Title));
    public static string Main_Add => Get(nameof(Main_Add));
    public static string Main_Edit => Get(nameof(Main_Edit));
    public static string Main_Remove => Get(nameof(Main_Remove));
    public static string Main_ListenInfo(string ip, int port) => Format(nameof(Main_ListenInfo), ip, port);
    public static string Col_On => Get(nameof(Col_On));
    public static string Col_Name => Get(nameof(Col_Name));
    public static string Col_Ip => Get(nameof(Col_Ip));
    public static string Col_Port => Get(nameof(Col_Port));
    public static string Col_Forwarded => Get(nameof(Col_Forwarded));
    public static string Main_StartWithWindows => Get(nameof(Main_StartWithWindows));
    public static string Main_Record => Get(nameof(Main_Record));
    public static string Main_StopRecording => Get(nameof(Main_StopRecording));
    public static string Main_Recording => Get(nameof(Main_Recording));
    public static string Record_SaveTitle => Get(nameof(Record_SaveTitle));
    public static string Record_Filter => Get(nameof(Record_Filter));

    // Tabs
    public static string Tab_Status => Get(nameof(Tab_Status));
    public static string Tab_Activity => Get(nameof(Tab_Activity));
    public static string Tab_Overlay => Get(nameof(Tab_Overlay));
    public static string Tab_Settings => Get(nameof(Tab_Settings));
    public static string Settings_StartupDesc => Get(nameof(Settings_StartupDesc));
    public static string Settings_AutoSplit => Get(nameof(Settings_AutoSplit));
    public static string Settings_AutoSplitDesc => Get(nameof(Settings_AutoSplitDesc));
    public static string Settings_UnitDesc => Get(nameof(Settings_UnitDesc));
    public static string Settings_LangDesc => Get(nameof(Settings_LangDesc));

    // Main window
    public static string Main_BackgroundNote => Get(nameof(Main_BackgroundNote));

    // Logging (Activity tab)
    public static string Log_Start => Get(nameof(Log_Start));
    public static string Log_Stop => Get(nameof(Log_Stop));
    public static string Log_ChangeFolder => Get(nameof(Log_ChangeFolder));
    public static string Log_OpenFolder => Get(nameof(Log_OpenFolder));
    public static string Log_Recording => Get(nameof(Log_Recording));
    public static string Log_FolderLabel(string path) => Format(nameof(Log_FolderLabel), path);

    // Overlay settings tab
    public static string Ov_Show => Get(nameof(Ov_Show));
    public static string Ov_TransparentBg => Get(nameof(Ov_TransparentBg));
    public static string Ov_TextColor => Get(nameof(Ov_TextColor));
    public static string Ov_BgColor => Get(nameof(Ov_BgColor));
    public static string Ov_Opacity => Get(nameof(Ov_Opacity));
    public static string Ov_Move => Get(nameof(Ov_Move));
    public static string Ov_DoneMoving => Get(nameof(Ov_DoneMoving));
    public static string Ov_MoveHint => Get(nameof(Ov_MoveHint));

    // Activity chart
    public static string Chart_Title(int minutes) => Format(nameof(Chart_Title), minutes);
    public static string Chart_Unit => Get(nameof(Chart_Unit));
    public static string Chart_Empty => Get(nameof(Chart_Empty));
    public static string Chart_Waiting => Get(nameof(Chart_Waiting));
    public static string Zoom_In => Get(nameof(Zoom_In));
    public static string Zoom_Out => Get(nameof(Zoom_Out));

    // Per-destination status dots
    public static string Dot_Forwarding => Get(nameof(Dot_Forwarding));
    public static string Dot_Idle => Get(nameof(Dot_Idle));
    public static string Dot_Disabled => Get(nameof(Dot_Disabled));
    public static string Dot_Error => Get(nameof(Dot_Error));
    public static string Dot_Tooltip(string ip, int port) => Format(nameof(Dot_Tooltip), ip, port);
    public static string Col_Status => Get(nameof(Col_Status));

    // Status
    public static string Status_Stopped => Get(nameof(Status_Stopped));
    public static string Status_WaitingForForza(string ip, int port) => Format(nameof(Status_WaitingForForza), ip, port);
    public static string Status_GameDetectedWaiting(string game) => Format(nameof(Status_GameDetectedWaiting), game);
    public static string Status_Connected(string game, int pps, string race, string readout)
        => Format(nameof(Status_Connected), game, pps, race, readout);
    public static string Status_RaceOn => Get(nameof(Status_RaceOn));
    public static string Status_RaceOff => Get(nameof(Status_RaceOff));
    public static string Readout_Gear(string gear, string speed) => Format(nameof(Readout_Gear), gear, speed);

    // Overlay
    public static string Overlay_Connected => Get(nameof(Overlay_Connected));
    public static string Overlay_NoData => Get(nameof(Overlay_NoData));

    // Remove confirm
    public static string Remove_Confirm(string name) => Format(nameof(Remove_Confirm), name);
    public static string Remove_Title => Get(nameof(Remove_Title));

    // Destination dialog
    public static string Dest_AddTitle => Get(nameof(Dest_AddTitle));
    public static string Dest_EditTitle => Get(nameof(Dest_EditTitle));
    public static string Dest_Preset => Get(nameof(Dest_Preset));
    public static string Dest_Name => Get(nameof(Dest_Name));
    public static string Dest_Ip => Get(nameof(Dest_Ip));
    public static string Dest_PortLabel => Get(nameof(Dest_PortLabel));
    public static string Dest_Enabled => Get(nameof(Dest_Enabled));
    public static string Dest_Ok => Get(nameof(Dest_Ok));
    public static string Dest_Cancel => Get(nameof(Dest_Cancel));
    public static string Dest_MissingName => Get(nameof(Dest_MissingName));
    public static string Dest_InvalidIp => Get(nameof(Dest_InvalidIp));

    // Welcome
    public static string Welcome_Title => Get(nameof(Welcome_Title));
    public static string Welcome_Intro => Get(nameof(Welcome_Intro));
    public static string Welcome_StepHeading => Get(nameof(Welcome_StepHeading));
    public static string Welcome_Path => Get(nameof(Welcome_Path));
    public static string Welcome_Note => Get(nameof(Welcome_Note));
    public static string Welcome_DontShow => Get(nameof(Welcome_DontShow));
    public static string Welcome_OpenGuide => Get(nameof(Welcome_OpenGuide));
    public static string Welcome_GetStarted => Get(nameof(Welcome_GetStarted));

    // Language selector
    public static string Lang_Label => Get(nameof(Lang_Label));
    public static string Lang_Auto => Get(nameof(Lang_Auto));
    public static string Lang_RestartNote => Get(nameof(Lang_RestartNote));

    // Companion apps (Settings tab + dialog)
    public static string Settings_Companions => Get(nameof(Settings_Companions));
    public static string Settings_CompanionsDesc => Get(nameof(Settings_CompanionsDesc));
    public static string Comp_AddTitle => Get(nameof(Comp_AddTitle));
    public static string Comp_EditTitle => Get(nameof(Comp_EditTitle));
    public static string Comp_Name => Get(nameof(Comp_Name));
    public static string Comp_Path => Get(nameof(Comp_Path));
    public static string Comp_Browse => Get(nameof(Comp_Browse));
    public static string Comp_Arguments => Get(nameof(Comp_Arguments));
    public static string Comp_Enabled => Get(nameof(Comp_Enabled));
    public static string Comp_BatNote => Get(nameof(Comp_BatNote));
    public static string Comp_MissingName => Get(nameof(Comp_MissingName));
    public static string Comp_MissingPath => Get(nameof(Comp_MissingPath));
    public static string Comp_FileFilter => Get(nameof(Comp_FileFilter));
    public static string Comp_RemoveTitle => Get(nameof(Comp_RemoveTitle));
    public static string Col_Path => Get(nameof(Col_Path));
    public static string Col_Args => Get(nameof(Col_Args));

    // Errors
    public static string Error_PortInUse(int port) => Format(nameof(Error_PortInUse), port);
    public static string Error_CompanionLaunch(string name, string error)
        => Format(nameof(Error_CompanionLaunch), name, error);
}
