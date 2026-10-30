"""Pure deployment guard tests. No administrator, processes or system mutation."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('linkgate_manage', ROOT / 'server/manage.py')
manage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manage)


class ConfigGuards(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='linkgate-config-')
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / 'frps.toml'
        self.good = (ROOT / 'server/frps.example.toml').read_text().replace(
            'REPLACE_WITH_64_LOWERCASE_HEX_CHARACTERS', 'a' * 64)

    def check(self, text):
        self.path.write_text(text, encoding='utf-8')
        return manage.validate_config(self.path)

    def test_exact_private_contract(self):
        result = self.check(self.good)
        self.assertEqual(result['bindPort'], 17400)
        self.assertEqual(result['proxyBindAddr'], '127.0.0.1')
        self.assertTrue(result['transport.tls.force'])

    def test_rejects_downgrade_or_extra_public_service(self):
        for text in [
            self.good.replace('force = true', 'force = false'),
            self.good.replace('proxyBindAddr = "127.0.0.1"', 'proxyBindAddr = "0.0.0.0"'),
            self.good + '\nwebServer.port = 17401\n',
            self.good + '\nvhostHTTPPort = 80\n',
            self.good + '\nnatHoleUDPBindPort = 17400\n',
            self.good + '\n[httpPlugins]\n',
            self.good + '\ntransport.tls.force = true\n',
        ]:
            with self.subTest(text=text[-60:]):
                with self.assertRaises(manage.Failure):
                    self.check(text)

    def test_rejects_bad_token_missing_or_inexact_types(self):
        for text in [
            self.good.replace('a' * 64, 'abc'),
            self.good.replace('a' * 64, 'A' * 64),
            self.good.replace('17400', '17401'),
            self.good.replace('force = true', 'force = 1'),
            self.good.replace('bindPort = 17400', 'bindPort = "17400"'),
            self.good.replace('auth.method = "token"', ''),
        ]:
            with self.subTest(text=text[-60:]):
                with self.assertRaises(manage.Failure):
                    self.check(text)

    def test_unit_pins_exact_nonprivileged_owner_and_paths(self):
        unit = manage.unit_text()
        self.assertIn('User=linkgate\nGroup=linkgate\n', unit)
        self.assertIn('ExecStart=/opt/linkgate/v0.1.0/frps -c /etc/linkgate/frps.toml', unit)
        self.assertIn('ProtectSystem=strict', unit)
        self.assertIn('NoNewPrivileges=true', unit)
        self.assertNotIn('root', unit)

    def test_listener_inode_accepts_v4_and_dual_stack_v6_wildcards_only(self):
        def row(host, port, state, inode):
            return ' 0: {}:{:04X} 00000000:0000 {} 0:0 0:0 0 0 0 {} 1'.format(host, port, state, inode)
        ipv4 = 'header\n' + row('0'*8, 17400, '0A', 111)
        ipv6 = 'header\n' + row('0'*32, 17400, '0A', 222)
        ignored = 'header\n' + '\n'.join([
            row('0100007F', 17400, '0A', 333),
            row('0'*32, 17401, '0A', 444),
            row('0'*32, 17400, '01', 555), 'truncated'])
        self.assertEqual(manage.listener_inodes([ipv4], 17400), {'socket:[111]'})
        self.assertEqual(manage.listener_inodes([ipv6], 17400), {'socket:[222]'})
        self.assertEqual(manage.listener_inodes([ipv4, ipv6, ignored], 17400),
                         {'socket:[111]', 'socket:[222]'})

    def test_recovery_accepts_only_incomplete_owned_phase_and_never_reinstalls(self):
        for phase in ('prepared', 'failed'):
            manage.recoverable_state({'phase': phase})
        for phase in ('active', 'stopped', 'other', None):
            with self.assertRaises(manage.Failure):
                manage.recoverable_state({'phase': phase})
        args = type('Args', (), {'apply': False})()
        with patch.object(manage, 'read_state', return_value={'phase': 'failed'}), \
             patch.object(manage, 'inspect_owned') as owned, \
             patch.object(manage, 'properties', return_value={'ActiveState':'inactive','MainPID':'0'}), \
             patch.object(manage, 'command') as mutate, patch.object(manage, 'write_state') as write:
            manage.recover(args)
            owned.assert_called_once_with({'phase': 'failed'})
            mutate.assert_not_called()
            write.assert_not_called()
        original = {'phase':'failed', 'owner':'retained-owner', 'files':{'server.key':'retained-hash'}}
        with patch.object(manage, 'read_state', return_value=original), \
             patch.object(manage, 'inspect_owned'), \
             patch.object(manage, 'properties', return_value={'ActiveState':'inactive','MainPID':'0'}), \
             patch.object(manage, 'verify_active'), \
             patch.object(manage, 'command') as mutate, patch.object(manage, 'write_state') as write:
            args.apply = True
            manage.recover(args)
            mutate.assert_called_once_with(['systemctl','enable','--now','linkgate-frps.service'])
            write.assert_called_once_with({'phase':'active','owner':'retained-owner','files':{'server.key':'retained-hash'}})
        with patch.object(manage, 'read_state', return_value={'phase': 'failed'}), \
             patch.object(manage, 'inspect_owned', side_effect=manage.Failure('missing owned file')), \
             patch.object(manage, 'command') as mutate, patch.object(manage, 'write_state') as write:
            args.apply = True
            with self.assertRaises(manage.Failure):
                manage.recover(args)
            mutate.assert_not_called()
            write.assert_not_called()


if __name__ == '__main__':
    unittest.main(verbosity=2)
