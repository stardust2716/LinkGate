//go:build !linux

package serverhost

import "os"

func privateDirectory(string) (os.FileInfo, error) { return nil, ErrPlatform }
func privateFile(string, int) (*os.File, error)    { return nil, ErrPlatform }
func syncDirectory(string) error                   { return ErrPlatform }
