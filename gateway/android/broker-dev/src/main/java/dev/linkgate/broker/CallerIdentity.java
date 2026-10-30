package dev.linkgate.broker;

import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.os.Build;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

final class CallerIdentity {
    private CallerIdentity() { }

    static String capture(PackageManager manager, int uid) {
        try {
            String first = snapshot(manager, uid);
            if (!first.equals(snapshot(manager, uid))) throw denied();
            return digest(first.getBytes(StandardCharsets.UTF_8));
        } catch (PackageManager.NameNotFoundException | IllegalArgumentException e) { throw denied(); }
    }

    @SuppressWarnings("deprecation")
    private static String snapshot(PackageManager manager, int uid) throws PackageManager.NameNotFoundException {
        String[] packages = manager.getPackagesForUid(uid);
        if (packages == null || packages.length != 1) throw denied();
        int flags = Build.VERSION.SDK_INT >= 28 ? PackageManager.GET_SIGNING_CERTIFICATES : PackageManager.GET_SIGNATURES;
        PackageInfo info = manager.getPackageInfo(packages[0], flags);
        if (info.applicationInfo == null || info.applicationInfo.uid != uid || info.sharedUserId != null || info.firstInstallTime <= 0) throw denied();
        Signature[] current, history;
        if (Build.VERSION.SDK_INT >= 28) {
            if (info.signingInfo == null) throw denied();
            current = info.signingInfo.getApkContentsSigners();
            history = info.signingInfo.hasMultipleSigners() ? current : info.signingInfo.getSigningCertificateHistory();
        } else { current = info.signatures; history = current; }
        return uid + "\n" + packages[0] + "\n" + info.firstInstallTime + "\n" + hashes(current) + "\n" + hashes(history);
    }

    private static String hashes(Signature[] values) {
        if (values == null || values.length == 0 || values.length > 32) throw denied();
        List<String> hashes = new ArrayList<>();
        for (Signature value : values) { if (value == null) throw denied(); hashes.add(digest(value.toByteArray())); }
        Collections.sort(hashes);
        return String.join(",", hashes);
    }
    private static String digest(byte[] value) {
        try {
            byte[] bytes = MessageDigest.getInstance("SHA-256").digest(value);
            StringBuilder output = new StringBuilder(64);
            for (byte item : bytes) { output.append(Character.forDigit((item >>> 4) & 15, 16)); output.append(Character.forDigit(item & 15, 16)); }
            return output.toString();
        } catch (NoSuchAlgorithmException e) { throw denied(); }
    }
    private static SecurityException denied() { return new SecurityException("Caller identity unavailable."); }
}
