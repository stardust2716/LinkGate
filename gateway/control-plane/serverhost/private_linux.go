//go:build linux

package serverhost

import (
	"os"
	"path/filepath"
	"syscall"

	"golang.org/x/sys/unix"
)

func noLinks(path string) error {
	if !filepath.IsAbs(path) || filepath.Clean(path) != path {
		return ErrStorage
	}
	for current := path; ; current = filepath.Dir(current) {
		info, e := os.Lstat(current)
		if e != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 {
			return ErrStorage
		}
		stat, ok := info.Sys().(*syscall.Stat_t)
		if !ok || stat.Uid != 0 && stat.Uid != uint32(os.Geteuid()) || info.Mode().Perm()&0022 != 0 && info.Mode()&os.ModeSticky == 0 {
			return ErrStorage
		}
		if current == filepath.Dir(current) {
			return nil
		}
	}
}
func privateDirectory(path string) (os.FileInfo, error) {
	if noLinks(path) != nil {
		return nil, ErrStorage
	}
	info, e := os.Lstat(path)
	if e != nil || !info.IsDir() || info.Mode().Perm() != 0700 {
		return nil, ErrStorage
	}
	stat, ok := info.Sys().(*syscall.Stat_t)
	if !ok || stat.Uid != uint32(os.Geteuid()) {
		return nil, ErrStorage
	}
	return info, nil
}
func privateFile(path string, flags int) (*os.File, error) {
	if _, e := privateDirectory(filepath.Dir(path)); e != nil {
		return nil, e
	}
	info, e := os.Lstat(path)
	if e != nil && !(os.IsNotExist(e) && flags&os.O_CREATE != 0 && flags&os.O_EXCL != 0) || e == nil && !info.Mode().IsRegular() {
		return nil, ErrStorage
	}
	fd, e := unix.Open(path, flags|unix.O_NOFOLLOW|unix.O_CLOEXEC|unix.O_NONBLOCK, 0600)
	if e != nil {
		return nil, ErrStorage
	}
	file := os.NewFile(uintptr(fd), path)
	info, e = file.Stat()
	if e != nil {
		file.Close()
		return nil, ErrStorage
	}
	stat, ok := info.Sys().(*syscall.Stat_t)
	if !ok || !info.Mode().IsRegular() || info.Mode().Perm() != 0600 || stat.Uid != uint32(os.Geteuid()) || stat.Nlink != 1 {
		file.Close()
		return nil, ErrStorage
	}
	return file, nil
}
func syncDirectory(path string) error {
	file, e := os.Open(path)
	if e != nil {
		return ErrStorage
	}
	defer file.Close()
	if file.Sync() != nil {
		return ErrStorage
	}
	return nil
}
