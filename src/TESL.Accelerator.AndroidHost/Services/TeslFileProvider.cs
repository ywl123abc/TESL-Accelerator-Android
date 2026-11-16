using System;
using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;

namespace TESL.Accelerator.AndroidHost.Services
{
    [ContentProvider(new[] { "com.tesl.accelerator.fileprovider" }, Exported = false, GrantUriPermissions = true)]
    public class TeslFileProvider : ContentProvider
    {
        public override bool OnCreate() => true;

        public override ParcelFileDescriptor? OpenFile(Android.Net.Uri uri, string mode)
        {
            string fileName = uri.LastPathSegment ?? "update.apk";
            var file = new Java.IO.File(Context?.CacheDir, fileName);
            if (!file.Exists())
            {
                file = new Java.IO.File(Context?.FilesDir, fileName);
            }

            if (!file.Exists())
            {
                throw new Java.IO.FileNotFoundException($"Cannot find file for URI: {uri}");
            }

            return ParcelFileDescriptor.Open(file, ParcelFileMode.ReadOnly);
        }

        public override string? GetType(Android.Net.Uri uri) => "application/vnd.android.package-archive";

        public override ICursor? Query(Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
        {
            string fileName = uri.LastPathSegment ?? "update.apk";
            var file = new Java.IO.File(Context?.CacheDir, fileName);
            if (!file.Exists())
            {
                file = new Java.IO.File(Context?.FilesDir, fileName);
            }

#pragma warning disable CA1422
            var cursor = new MatrixCursor(new[] { Android.Provider.IOpenableColumns.DisplayName, Android.Provider.IOpenableColumns.Size });
            if (file.Exists())
            {
                cursor.AddRow(new Java.Lang.Object[] { new Java.Lang.String(file.Name), new Java.Lang.Long(file.Length()) });
            }
#pragma warning restore CA1422
            return cursor;
        }

        public override Android.Net.Uri? Insert(Android.Net.Uri uri, ContentValues? values) => null;
        public override int Delete(Android.Net.Uri uri, string? selection, string[]? selectionArgs) => 0;
        public override int Update(Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;
    }
}
