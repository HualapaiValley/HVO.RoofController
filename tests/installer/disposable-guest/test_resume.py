"""Offline fixture tests only: no real guest, Docker, SSH or account mutations."""
import copy
import json
import os
from pathlib import Path
import shutil
import stat
import tempfile
import unittest
from unittest.mock import Mock, patch

import guestctl
import reconfigure_guest
import resume_state
import run_guest


class ResumeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)/'root'
        self.state = Path(self.temp.name)/'state'
        self.root.mkdir(); self.state.mkdir()
        (self.root/'work.qcow2').write_bytes(b'fixture-existing-disk-do-not-replace')
        (self.root/'base.qcow2').write_bytes(b'fixture-base')
        (self.root/'guestctl.py').write_text('fixture original launcher')
        for name in resume_state.ACCESS_FILES:
            (self.state/name).write_bytes(('inert fixture '+name).encode())
        (self.state/'seed').mkdir()
        (self.state/'seed/user-data').write_text('fixture-run')

    def stage(self):
        return resume_state.stage(self.root,self.state,'a'*64,'fixture-run','old-boot')

    def test_checkpoint_reuses_disk_access_and_never_creates_disk(self):
        disk=self.root/'work.qcow2'; before=disk.read_bytes(); inode=disk.stat().st_ino
        original={name:(self.state/name).read_bytes() for name in resume_state.ACCESS_FILES}
        self.stage()
        self.assertEqual(disk.read_bytes(),before)
        shutil.rmtree(self.state)  # Models fresh tmpfs after a clean container stop.
        checker=Mock()
        result=resume_state.restore(self.root,self.state,checker=checker)
        checker.assert_called_once_with(disk,self.root/'base.qcow2')
        self.assertEqual(disk.stat().st_ino,inode)
        self.assertEqual(disk.read_bytes(),before)
        self.assertEqual((self.root/'resume/disk-before-first-resume.qcow2').read_bytes(),before)
        self.assertTrue(result['checkpoint_complete'])
        for name,value in original.items():
            self.assertEqual((self.state/name).read_bytes(),value)
            self.assertEqual(stat.S_IMODE((self.state/name).stat().st_mode),0o600)
        self.assertEqual(stat.S_IMODE((self.root/'resume').stat().st_mode),0o700)

    def test_existing_checkpoint_is_not_overwritten(self):
        self.stage()
        with self.assertRaises(FileExistsError): self.stage()

    def test_tampered_access_is_refused_before_disk_check(self):
        self.stage();shutil.rmtree(self.state)
        (self.root/'resume/known_hosts').write_text('changed fixture')
        checker=Mock()
        with self.assertRaisesRegex(RuntimeError,'access material changed'):
            resume_state.restore(self.root,self.state,checker=checker)
        checker.assert_not_called()
        self.assertFalse(self.state.exists())

    def test_replaced_disk_is_refused(self):
        self.stage();shutil.rmtree(self.state)
        (self.root/'work.qcow2').rename(self.root/'original.qcow2')
        (self.root/'work.qcow2').write_bytes(b'another-disk')
        with self.assertRaisesRegex(RuntimeError,'disk was replaced'):
            resume_state.restore(self.root,self.state,checker=Mock())

    def test_second_runtime_and_invalid_disk_are_refused(self):
        self.stage()
        with self.assertRaisesRegex(RuntimeError,'Runtime state already exists'):
            resume_state.restore(self.root,self.state,checker=Mock())
        shutil.rmtree(self.state)
        with self.assertRaisesRegex(RuntimeError,'invalid qcow'):
            resume_state.restore(self.root,self.state,checker=Mock(side_effect=RuntimeError('invalid qcow')))
        self.assertFalse((self.root/'resume/disk-before-first-resume.qcow2').exists())

    def test_later_start_preserves_first_checkpoint(self):
        self.stage();shutil.rmtree(self.state)
        resume_state.restore(self.root,self.state,checker=Mock())
        backup=(self.root/'resume/disk-before-first-resume.qcow2').read_bytes()
        (self.root/'work.qcow2').write_bytes(b'fixture later app state')
        shutil.rmtree(self.state)
        resume_state.restore(self.root,self.state,checker=Mock())
        self.assertEqual((self.root/'resume/disk-before-first-resume.qcow2').read_bytes(),backup)
        self.assertEqual((self.root/'work.qcow2').read_bytes(),b'fixture later app state')

    def test_fresh_serve_refuses_existing_disk(self):
        actual_exists=Path.exists
        def exists(p):
            return True if str(p)=='/.dockerenv' else actual_exists(p)
        with patch.object(guestctl,'ROOT',self.root),patch.object(Path,'exists',exists),patch('guestctl.subprocess.run') as run:
            with self.assertRaisesRegex(SystemExit,'Existing writable guest disk'): guestctl.serve()
            run.assert_not_called()

    def test_resume_serve_uses_four_cpus_without_fresh_setup(self):
        self.stage();shutil.rmtree(self.state)
        actual_exists=Path.exists
        def exists(p):
            return True if str(p)=='/.dockerenv' else actual_exists(p)
        with patch.object(Path,'exists',exists),patch.object(guestctl,'ROOT',self.root),patch.object(guestctl,'STATE',self.state),patch('resume_state.restore',return_value={'vcpus':4,'guest_memory_mib':3072}) as restore,patch('guestctl.launch') as launch,patch('guestctl.subprocess.run') as run:
            guestctl.serve()
            restore.assert_called_once_with(self.root,self.state)
            launch.assert_called_once_with(self.root/'work.qcow2',4,3072)
            run.assert_not_called()


class GuardTests(unittest.TestCase):
    def test_limits_allow_only_the_two_requested_fields(self):
        before={'NanoCpus':2*10**9,'Memory':4*1024**3,'NetworkMode':'none','MemorySwap':8*1024**3}
        after=dict(before,NanoCpus=4*10**9,Memory=6*1024**3)
        reconfigure_guest.limits_only(before,after)
        for key,value in [('NetworkMode','host'),('MemorySwap',12*1024**3),('Memory',8*1024**3)]:
            with self.subTest(key=key),self.assertRaises(RuntimeError):
                reconfigure_guest.limits_only(before,dict(after,**{key:value}))

    def test_guest_continuity_requires_uuid_run_boot_and_cpu(self):
        before={'run_id':'run','root_filesystem_uuid':'rootfs','boot_id':'old','cpus':2}
        after=dict(before,boot_id='new',cpus=4)
        reconfigure_guest.verify_guest_continuity(before,after)
        for key,value in [('run_id','other'),('root_filesystem_uuid','other'),('boot_id','old'),('cpus',2)]:
            with self.subTest(key=key),self.assertRaises(RuntimeError):
                reconfigure_guest.verify_guest_continuity(before,dict(after,**{key:value}))

    def test_exact_container_identity_is_required_before_guest_exec(self):
        with patch('reconfigure_guest.inspect',return_value={'id':'wrong','state':'running'}),patch('reconfigure_guest.docker') as docker:
            with self.assertRaisesRegex(RuntimeError,'Container identity changed'):
                reconfigure_guest.identity('expected','run')
            docker.assert_not_called()

    def test_inspector_rejects_other_profiles_and_weakened_isolation(self):
        h={'NetworkMode':'none','Privileged':False,'Binds':None,'Devices':[],'PortBindings':{},'CapDrop':['ALL'],
           'SecurityOpt':['no-new-privileges'],'Memory':4*1024**3,'NanoCpus':2*10**9,'PidsLimit':128}
        data={'Id':'owned','Config':{'Labels':{'hvo.disposable-guest':'auth-integration'}},'HostConfig':h,'State':{'Status':'running'}}
        def inspect(value):
            with patch('run_guest.docker',return_value=Mock(stdout=json.dumps([value]))): return run_guest.inspect()
        self.assertEqual(inspect(data)['cpus'],2)
        expanded=copy.deepcopy(data);expanded['HostConfig'].update(NanoCpus=4*10**9,Memory=6*1024**3)
        self.assertEqual(inspect(expanded)['cpus'],4)
        for key,value in [('NanoCpus',6*10**9),('Memory',8*1024**3),('Privileged',True),('NetworkMode','host'),('Binds',['/tmp:/host']),('Devices',[{'PathOnHost':'/dev/kvm'}]),('CapDrop',[])]:
            bad=copy.deepcopy(data);bad['HostConfig'][key]=value
            with self.subTest(key=key),self.assertRaises(AssertionError): inspect(bad)


if __name__=='__main__': unittest.main()
