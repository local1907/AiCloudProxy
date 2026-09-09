# Assets

This folder holds the images and icons used by **AI Cloud Proxy**.

Everything placed here is automatically embedded into the app as a WPF resource
(see the `<Resource Include="Assets\**\*" />` entry in `AiCloudProxy.csproj`) and
can be referenced from XAML like:

```xml
Source="pack://application:,,,/Assets/my-image.png"
```

## Files

- `qr-code.png` — Buy Me a Coffee QR code shown on the **Proxy Server** tab.
- `icon.ico` — App / taskbar / window title bar icon (wired up via `ApplicationIcon` in `AiCloudProxy.csproj` and the `Icon` property on `MainWindow.xaml`).

## Adding an app / taskbar icon

1. Put your icon here as `icon.ico` (an `.ico` file, ideally 256×256).
2. In `AiCloudProxy.csproj`, uncomment / add the line in the first `<PropertyGroup>`:
   ```xml
   <ApplicationIcon>Assets\icon.ico</ApplicationIcon>
   ```
3. Rebuild — the icon will appear on the EXE, taskbar, and window title bar.
