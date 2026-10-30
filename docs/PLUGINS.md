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

## Give the AI new abilities

An in-process tool can let OverDesk's AI use it. Set `"ai": true` in the manifest and implement
`Overdesk.Core.Plugins.IAiToolSource` next to `IToolPlugin`:

```csharp
public sealed class ConverterPlugin : IToolPlugin, IAiToolSource
{
    public object CreateView() => new ConverterView();

    public IReadOnlyList<PluginAiTool> AiTools { get; } = new[]
    {
        new PluginAiTool(
            "convert_files",                       // letters, digits, _ and -
            "Convert files to another format…",    // tell the model when to use it
            Schema,                                // a JSON Schema object, as text
            async (json, cancellation) => "…text for the model…")
        {
            Label = json => "Converting 2 files to PNG",   // optional: the line shown in the chat
            Approval = json => "Delete these files?",      // optional: ask the user before every call
        },
    };
}
```

Every AI provider gets the tool: the API ones call it directly, Claude Code and Gemini CLI through OverDesk's local MCP
server. Throw to report an error; the model sees the message. The same plugin instance serves the panel and the AI.

## Take dropped files

When the user drops files on the side panel while your tool is open, OverDesk hands them to your view if it implements
`Overdesk.Core.Plugins.IFileDropTarget` (`DropHint` is the line on the drop overlay). Otherwise the files open as tabs.

## Bundled tools

All of them ship with OverDesk, give the AI their abilities, and are good examples:

- `tools/converter`: images (PNG, JPG, WebP, GIF, BMP, ICO, TGA; HEIC, AVIF, PSD, RAW, SVG, TIFF and PDF pages on macOS), data
  (JSON, YAML, TOML, XML, plist, INI, .env, .properties, CSV, TSV, JSON Lines, Excel; tables to Markdown, HTML or SQL), Markdown and
  documents, subtitles, audio and video (ffmpeg), archives (ZIP, TAR, TAR.GZ, GZ), fonts (TTF/OTF ↔ WOFF) and 3D models (OBJ, STL,
  PLY → glTF, GLB). Any file can be zipped.
- `tools/devkit`: about fifty developer utilities in groups (format, encode, hash, generate, text, time, web, numbers, code).
- `tools/texture`: PBR maps from one texture (normal, height, AO, roughness, metallic, curvature, ORM / HDRP mask) for Unity, Unreal,
  Godot and Blender.
- `tools/icons`: every app icon file from one image (.icns, .ico, iOS AppIcon set, Android mipmaps, favicons).
- `tools/sprites`: pack sprites into an atlas + JSON, or slice a sheet into frames.

`tools/shared` is not a tool: it holds code the bundled tools link into their own assemblies (image reading and writing, panel
building blocks). `scripts/package-*` copies every folder with a manifest into the app.

## Rules of thumb

- Keep in-process tools light: they run on OverDesk's UI thread. Do slow work with `Task.Run`.
- Do not store secrets in plain files.
- Pick a stable `id` and keep it across versions.
