namespace Capacitor.App.Platform.Windows;

/// Interface ids and vtable slots from the WebView2 SDK's WebView2.h. A slot counts from the start
/// of the vtable, IUnknown's three methods included, and every derived interface keeps its base's
/// slots, so a slot read off an older interface holds on a newer one. WebView2 never reorders a
/// shipped interface; a new member always arrives as a new interface.
static class WebView2Abi {
    public static readonly Guid IUnknown = new("00000000-0000-0000-c000-000000000046");

    public static readonly Guid ICoreWebView2_4 = new("20d02d59-6df2-42dc-bd06-f98a694b1302");
    public static readonly Guid ICoreWebView2Frame2 = new("7a6a5834-d185-4dbf-b63f-4a9bc43107d4");
    public static readonly Guid ICoreWebView2Frame7 = new("3598cfa2-d85d-5a9f-9228-4dde1f59ec64");
    public static readonly Guid ICoreWebView2Settings3 = new("fdb5ab74-af33-4854-84f0-0a631deb5eba");

    public static readonly Guid NavigationStartingEventHandler = new("9adbe429-f36d-432b-9ddc-f8881fbd76e3");
    public static readonly Guid NavigationCompletedEventHandler = new("d33a35bf-1c49-4f98-93ab-006e0533fe1c");
    public static readonly Guid NewWindowRequestedEventHandler = new("d4c185fe-c81c-4989-97af-2d3fa7ab5651");
    public static readonly Guid FrameCreatedEventHandler = new("38059770-9baa-11eb-a8b3-0242ac130003");
    public static readonly Guid FrameNavigationStartingEventHandler = new("e79908bf-2d5d-4968-83db-263fea2c1da3");
    public static readonly Guid FrameChildFrameCreatedEventHandler = new("569e40e7-46b7-563d-83ae-1073155664d7");
    public static readonly Guid AddScriptToExecuteOnDocumentCreatedCompletedHandler = new("b99369f3-9b11-47b5-bc6f-8e7895fcea17");
    public static readonly Guid ExecuteScriptCompletedHandler = new("49511172-cc67-4bca-9923-137112f4c4cc");

    public const int QueryInterface = 0;
    public const int AddRef = 1;
    public const int Release = 2;

    // ICoreWebView2
    public const int CoreGetSettings = 3;
    public const int CoreNavigateToString = 6;
    public const int CoreAddNavigationStarting = 7;
    public const int CoreAddNavigationCompleted = 15;
    public const int CoreRemoveNavigationCompleted = 16;
    public const int CoreAddFrameNavigationStarting = 17;
    public const int CoreAddScriptToExecuteOnDocumentCreated = 27;
    public const int CoreExecuteScript = 29;
    public const int CoreAddNewWindowRequested = 44;
    // ICoreWebView2_4
    public const int Core4AddFrameCreated = 73;

    // ICoreWebView2Frame2
    public const int Frame2AddNavigationStarting = 11;
    // ICoreWebView2Frame7
    public const int Frame7AddFrameCreated = 30;
    // ICoreWebView2FrameCreatedEventArgs
    public const int FrameCreatedArgsGetFrame = 3;

    // ICoreWebView2NavigationStartingEventArgs
    public const int NavigationStartingArgsGetUri = 3;
    public const int NavigationStartingArgsGetCancel = 7;
    public const int NavigationStartingArgsPutCancel = 8;
    // ICoreWebView2NavigationCompletedEventArgs
    public const int NavigationCompletedArgsGetIsSuccess = 3;
    // ICoreWebView2NewWindowRequestedEventArgs
    public const int NewWindowRequestedArgsPutHandled = 6;

    // ICoreWebView2Settings
    public const int SettingsPutIsStatusBarEnabled = 10;
    public const int SettingsGetAreDevToolsEnabled = 11;
    public const int SettingsPutAreDevToolsEnabled = 12;
    public const int SettingsPutAreDefaultContextMenusEnabled = 14;
    // ICoreWebView2Settings3
    public const int Settings3PutAreBrowserAcceleratorKeysEnabled = 24;
}
