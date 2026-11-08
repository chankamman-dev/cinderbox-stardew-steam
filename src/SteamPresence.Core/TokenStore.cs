using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Android.Security.Keystore;
using Android.Util;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace SteamPresence.Core;

// The mod's own Steam login (from scanning the QR code), for players who never signed in to Steam
// in Cinderbox. Kept in Cinderbox's private files dir (not /sdcard), encrypted with an AndroidKeyStore
// key of the mod's own alias, the same way Cinderbox protects its login. Cinderbox's data is not
// touched.
static class TokenStore
{
    const string Alias = "steampresence_login";

    sealed class Saved
    {
        public string? Account { get; set; }
        public string? Token { get; set; }
    }

    /// <summary>The mod's folder in Cinderbox's private files dir.</summary>
    public static string Dir() => Path.Combine(Android.App.Application.Context.FilesDir!.AbsolutePath, "steam-presence");

    static string FilePath() => Path.Combine(Dir(), "login.enc");

    public static (string account, string token)? Load(Action<string> warn)
    {
        try
        {
            string path = FilePath();
            if (!File.Exists(path)) return null;
            string? json = Decrypt(File.ReadAllText(path));
            if (json == null) { warn("扫码登录的记录解不开（密钥没了？），需要重新扫码"); return null; }
            var s = JsonSerializer.Deserialize<Saved>(json);
            return string.IsNullOrEmpty(s?.Account) || string.IsNullOrEmpty(s?.Token) ? null : (s!.Account!, s.Token!);
        }
        catch (Exception e)
        {
            warn($"读取扫码登录记录失败：{e.Message}");
            return null;
        }
    }

    public static bool Save(string account, string token, Action<string> warn)
    {
        try
        {
            string path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(new Saved { Account = account, Token = token });
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Encrypt(json));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            warn($"保存扫码登录失败（这次能用，下次要重扫）：{e.Message}");
            return false;
        }
    }

    public static void Forget()
    {
        try { File.Delete(FilePath()); } catch { }
    }

    static string Encrypt(string plain)
    {
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.EncryptMode, Key());
        byte[] iv = cipher.GetIV()!;
        byte[] body = cipher.DoFinal(Encoding.UTF8.GetBytes(plain))!;
        var all = new byte[1 + iv.Length + body.Length];
        all[0] = (byte)iv.Length;
        Array.Copy(iv, 0, all, 1, iv.Length);
        Array.Copy(body, 0, all, 1 + iv.Length, body.Length);
        return Base64.EncodeToString(all, Base64Flags.NoWrap)!;
    }

    static string? Decrypt(string text)
    {
        try
        {
            byte[] all = Base64.Decode(text, Base64Flags.NoWrap)!;
            int ivLen = all[0];
            var iv = new byte[ivLen];
            Array.Copy(all, 1, iv, 0, ivLen);
            var body = new byte[all.Length - 1 - ivLen];
            Array.Copy(all, 1 + ivLen, body, 0, body.Length);
            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(128, iv));
            return Encoding.UTF8.GetString(cipher.DoFinal(body)!);
        }
        catch
        {
            return null;
        }
    }

    static ISecretKey Key()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        if (store.ContainsAlias(Alias))
            return ((KeyStore.SecretKeyEntry)store.GetEntry(Alias, null)!).SecretKey!;
        var gen = KeyGenerator.GetInstance("AES", "AndroidKeyStore")!;
        gen.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Decrypt | KeyStorePurpose.Encrypt)
            .SetBlockModes("GCM").SetEncryptionPaddings("NoPadding").SetKeySize(256).Build());
        return gen.GenerateKey()!;
    }
}
