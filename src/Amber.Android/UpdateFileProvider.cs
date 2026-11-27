using Android.App;
using Android.Content;

namespace Amber.Android;

[ContentProvider(["nz.work.minecraft.jay.updates"], Exported = false, GrantUriPermissions = true)]
[MetaData("android.support.FILE_PROVIDER_PATHS", Resource = "@xml/jay_update_paths")]
public sealed class UpdateFileProvider : AndroidX.Core.Content.FileProvider { }
