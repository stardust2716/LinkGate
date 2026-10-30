package com.nebula.linkgate;

import android.content.Context;
import android.util.AtomicFile;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;

final class ProfileStore {
    private ProfileStore() {}
    private static AtomicFile profile(Context context) {
        return new AtomicFile(new File(context.getFilesDir(), "profile.json"));
    }
    static Profile load(Context context) throws Exception {
        try (InputStream input = profile(context).openRead()) { return Profile.parse(readBounded(input)); }
    }
    static void save(Context context, Profile value) throws IOException {
        AtomicFile target = profile(context);
        FileOutputStream stream = null;
        try {
            stream = target.startWrite();
            stream.write(value.encoded());
            target.finishWrite(stream);
        } catch (IOException error) {
            if (stream != null) target.failWrite(stream);
            throw error;
        }
    }
    static byte[] readBounded(InputStream input) throws IOException {
        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = input.read(buffer)) != -1) {
            if (out.size() + count > Profile.MAX_BYTES) throw new IOException("配对文件超过 128 KiB。");
            out.write(buffer, 0, count);
        }
        return out.toByteArray();
    }
    static File prepare(Context context, Profile value) throws IOException {
        File runtime = new File(context.getFilesDir(), "runtime");
        if (!runtime.isDirectory() && !runtime.mkdir()) throw new IOException("无法准备私有运行目录。");
        writePrivate(new File(runtime, "ca.pem"), value.trustedCaPem.getBytes(java.nio.charset.StandardCharsets.US_ASCII));
        File config = new File(runtime, "frpc.toml");
        writePrivate(config, value.visitorToml(new File(runtime, "ca.pem").getAbsolutePath())
                .getBytes(java.nio.charset.StandardCharsets.UTF_8));
        return config;
    }
    private static void writePrivate(File file, byte[] contents) throws IOException {
        AtomicFile target = new AtomicFile(file);
        FileOutputStream stream = null;
        try {
            stream = target.startWrite(); stream.write(contents); target.finishWrite(stream);
            if (!file.setReadable(false, false) || !file.setWritable(false, false) ||
                    !file.setReadable(true, true) || !file.setWritable(true, true)) {
                throw new IOException("无法限制私有文件权限。");
            }
        } catch (IOException error) {
            if (stream != null) target.failWrite(stream);
            throw error;
        }
    }
}
