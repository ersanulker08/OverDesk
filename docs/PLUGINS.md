# Writing an OverDesk tool

Tools live in the panel on the right. A tool is a **folder** that contains an `overdesk.plugin.json` manifest.

## Where OverDesk looks

- `<settings folder>/plugins/<your-tool>/`. The settings folder is shown under **Settings → Data**:
  `~/Library/Application Support/Overdesk` on macOS, `%APPDATA%\Overdesk` on Windows.
- `tools/<your-tool>/` next to the app, for tools that ship with OverDesk.

Restart OverDesk after adding a folder. Manifests with problems are skipped; they never stop the app.

## The manifest

```jsonc
{
  "id": "yourname.converter",      // unique, reverse-DNS style
  "name": "Converter",             // shown in the panel header
  "version": "1.0.0",
  "description": "Converts files between formats",
  "icon": "IconSparkle",           // optional: a built-in icon name, otherwise the first letter is shown

  // Either: a .NET assembly loaded into OverDesk
  "run": "inProcess",
  "assembly": "Converter.dll",
  "typeName": "YourName.Converter.ConverterPlugin",

  // Or: any program, in any language, started and placed inside the panel
  // "run": "process",
  // "entry": "bin/converter"      // relative to this folder, or absolute
}
```

### In-process (C#)

Implement `Overdesk.Core.Plugins.IToolPlugin` and return an Avalonia `Control` from `CreateView()`. The control picks up
OverDesk's theme automatically. Reference `Overdesk.Core` and `Avalonia` but do not copy them into your plugin folder,
because the host provides both. [samples/HelloTool](../samples/HelloTool) shows the whole setup:

```bash
dotnet build samples/HelloTool
cp -R samples/HelloTool/plugin "<settings folder>/plugins/hello"
```

### Separate process

Any executable. OverDesk starts it and places its window inside the panel. It cannot crash OverDesk, but window
placement follows the same platform rules as program tabs: reparented on Windows, glued on macOS.

## Rules of thumb

- Keep in-process tools light: they run on OverDesk's UI thread. Do slow work with `Task.Run`.
- Do not store secrets in plain files.
- Pick a stable `id` and keep it across versions.
