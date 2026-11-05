using System.Runtime.InteropServices;

namespace Overdesk.Platform.MacOS;

internal sealed unsafe partial class MacWebView
{
    private const int ModalResponseOk = 1;
    private const int AlertFirstButton = 1000;

    /// <summary>
    /// An NSAlert on the view's window. <paramref name="done"/> gets the index of the button that was pressed.
    /// Without a window (the tab is not on stage) it blocks as an application-modal alert instead.
    /// </summary>
    private static void ShowAlert(IntPtr webView, string message, string? detail, string[] buttons, IntPtr accessory, Action<int> done)
    {
        var alert = ObjC.Call(ObjC.Call(ObjC.Class("NSAlert"), "alloc"), "init");
        ObjC.Call(alert, "setMessageText:", ObjC.NSString(message));
        if (detail is not null) ObjC.Call(alert, "setInformativeText:", ObjC.NSString(detail));
        foreach (var button in buttons) ObjC.Call(alert, "addButtonWithTitle:", ObjC.NSString(button));
        if (accessory != IntPtr.Zero) ObjC.Call(alert, "setAccessoryView:", accessory);

        void Finish(int response)
        {
            try { done(response - AlertFirstButton); }
            finally { ObjC.Call(alert, "release"); }
        }

        var window = ObjC.Call(webView, "window");
        if (window == IntPtr.Zero)
        {
            Finish(ObjC.CallInt(alert, "runModal"));
            return;
        }
        var block = ObjC.CompletionBlock((response, _) => Finish((int)response));
        ObjC.Call(alert, "beginSheetModalForWindow:completionHandler:", window, block);
        ObjC.ReleaseBlock(block);
    }

    private static partial class UIDelegate
    {
        private static void AddPanels(IntPtr cls)
        {
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> openPanel = &RunOpenPanel;
            ObjC.class_addMethod(cls, ObjC.Selector("webView:runOpenPanelWithParameters:initiatedByFrame:completionHandler:"),
                (IntPtr)openPanel, "v@:@@@@?");
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> alert = &RunAlert;
            ObjC.class_addMethod(cls, ObjC.Selector("webView:runJavaScriptAlertPanelWithMessage:initiatedByFrame:completionHandler:"),
                (IntPtr)alert, "v@:@@@@?");
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> confirm = &RunConfirm;
            ObjC.class_addMethod(cls, ObjC.Selector("webView:runJavaScriptConfirmPanelWithMessage:initiatedByFrame:completionHandler:"),
                (IntPtr)confirm, "v@:@@@@?");
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> prompt = &RunPrompt;
            ObjC.class_addMethod(cls, ObjC.Selector("webView:runJavaScriptTextInputPanelWithPrompt:defaultText:initiatedByFrame:completionHandler:"),
                (IntPtr)prompt, "v@:@@@@@?");
        }

        [UnmanagedCallersOnly]
        private static void RunOpenPanel(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr parameters, IntPtr frame, IntPtr completion)
        {
            try
            {
                var handler = ObjC.CopyBlock(completion);
                var panel = ObjC.Call(ObjC.Call(ObjC.Class("NSOpenPanel"), "openPanel"), "retain");
                ObjC.CallBoolArg(panel, "setCanChooseFiles:", true);
                ObjC.CallBoolArg(panel, "setCanChooseDirectories:", ObjC.CallBool(parameters, "allowsDirectories"));
                ObjC.CallBoolArg(panel, "setAllowsMultipleSelection:", ObjC.CallBool(parameters, "allowsMultipleSelection"));

                var block = ObjC.CompletionBlock((response, _) =>
                {
                    ObjC.InvokeBlock(handler, (int)response == ModalResponseOk ? ObjC.Call(panel, "URLs") : IntPtr.Zero);
                    ObjC.ReleaseBlock(handler);
                    ObjC.Call(panel, "release");
                });
                var window = ObjC.Call(webView, "window");
                if (window == IntPtr.Zero) ObjC.Call(panel, "beginWithCompletionHandler:", block);
                else ObjC.Call(panel, "beginSheetModalForWindow:completionHandler:", window, block);
                ObjC.ReleaseBlock(block);
            }
            catch (Exception) { /* never let an exception cross into Objective-C */ }
        }

        [UnmanagedCallersOnly]
        private static void RunAlert(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr message, IntPtr frame, IntPtr completion)
        {
            try
            {
                var handler = ObjC.CopyBlock(completion);
                ShowAlert(webView, PageSays(frame), ObjC.ReadNSString(message), new[] { "OK" }, IntPtr.Zero, _ =>
                {
                    ObjC.InvokeBlock(handler, IntPtr.Zero);
                    ObjC.ReleaseBlock(handler);
                });
            }
            catch (Exception) { }
        }

        [UnmanagedCallersOnly]
        private static void RunConfirm(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr message, IntPtr frame, IntPtr completion)
        {
            try
            {
                var handler = ObjC.CopyBlock(completion);
                ShowAlert(webView, PageSays(frame), ObjC.ReadNSString(message), new[] { "OK", "Cancel" }, IntPtr.Zero, button =>
                {
                    ObjC.InvokeBlock(handler, button == 0 ? 1 : 0);
                    ObjC.ReleaseBlock(handler);
                });
            }
            catch (Exception) { }
        }

        [UnmanagedCallersOnly]
        private static void RunPrompt(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr prompt, IntPtr defaultText, IntPtr frame, IntPtr completion)
        {
            try
            {
                var handler = ObjC.CopyBlock(completion);
                var field = ObjC.CallRectArg(ObjC.Call(ObjC.Class("NSTextField"), "alloc"), "initWithFrame:", new CGRect { Width = 300, Height = 24 }, IntPtr.Zero);
                if (defaultText != IntPtr.Zero) ObjC.Call(field, "setStringValue:", defaultText);
                ShowAlert(webView, PageSays(frame), ObjC.ReadNSString(prompt), new[] { "OK", "Cancel" }, field, button =>
                {
                    ObjC.InvokeBlock(handler, button == 0 ? ObjC.Call(field, "stringValue") : IntPtr.Zero);
                    ObjC.ReleaseBlock(handler);
                    ObjC.Call(field, "release");
                });
            }
            catch (Exception) { }
        }

        /// <summary>Names the site, so a page cannot pass its dialog off as one of Overdesk's own.</summary>
        private static string PageSays(IntPtr frame)
        {
            var host = ObjC.ReadNSString(ObjC.Call(ObjC.Call(frame, "securityOrigin"), "host"));
            return string.IsNullOrEmpty(host) ? "This page says" : host + " says";
        }
    }
}
