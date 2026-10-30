#!/usr/bin/env python3
"""Read-only LinkGate wire accounting on ECS; stores no packet contents.

Run as administrator: python3 ecs_traffic_window.py --seconds 90
Only TCP 17400 on the explicitly selected interface is captured. Does not
alter firewall rules. A bounded tcpdump child is terminated on every exit.
Use Python 3.6+ on ECS. Output contains aggregate counters, no IP addresses.
"""
import argparse
import json
import os
import select
import struct
import subprocess
import time
import threading
import re
import stat


def exact(stream, size, deadline, stop_file=None):
    result = bytearray()
    while len(result) < size:
        if stop_file and os.path.exists(stop_file):
            raise TimeoutError()
        if time.monotonic() >= deadline:
            raise TimeoutError()
        ready, _, _ = select.select([stream], [], [], min(1, deadline-time.monotonic()))
        if not ready:
            continue
        data = os.read(stream.fileno(), size-len(result))
        if not data:
            raise EOFError()
        result.extend(data)
    return bytes(result)


def measure(interface, seconds, stop_file=None):
    active = subprocess.check_output(['systemctl','show','linkgate-frps.service','--property=MainPID','--value'], universal_newlines=True).strip()
    if not active.isdigit() or int(active) < 1 or os.readlink('/proc/'+active+'/exe') != '/opt/linkgate/v0.1.0/frps':
        raise RuntimeError('Expected owned LinkGate server is not active')
    start = time.time()
    deadline = time.monotonic()+seconds
    command = ['tcpdump','-i',interface,'-nn','-s','96','-U','-w','-','tcp port 17400']
    child = subprocess.Popen(command,stdout=subprocess.PIPE,stderr=subprocess.PIPE,bufsize=0)
    capture_stats=[]
    def drain():
        for line in iter(child.stderr.readline,b''):
            match=re.match(rb'^(\d+) packets dropped by kernel',line[:256])
            if match: capture_stats.append(int(match.group(1)))
    reader=threading.Thread(target=drain);reader.daemon=True;reader.start()
    result = dict(schemaVersion=1, startedUtcMs=int(start*1000), seconds=seconds,
                  ingressIpBytes=0,egressIpBytes=0,ingressTcpPayloadBytes=0,egressTcpPayloadBytes=0,
                  packets=0,unparsedPackets=0, scope='interface-tcp-17400', captureComplete=False)
    buckets={}
    try:
        header = exact(child.stdout,24,deadline,stop_file)
        endian = '<' if header[:4] == b'\xd4\xc3\xb2\xa1' else '>' if header[:4] == b'\xa1\xb2\xc3\xd4' else None
        if endian is None or struct.unpack(endian+'I',header[20:24])[0] != 1:
            raise RuntimeError('Expected Ethernet PCAP')
        while time.monotonic() < deadline:
            packet = exact(child.stdout,16,deadline,stop_file)
            second,_,captured,wire = struct.unpack(endian+'IIII',packet)
            if captured > 96 or wire > 1048576:
                raise RuntimeError('Invalid PCAP record')
            frame = exact(child.stdout,captured,deadline,stop_file)
            result['packets'] += 1
            try:
                kind=struct.unpack('!H',frame[12:14])[0]; offset=14
                if kind==0x8100:
                    kind=struct.unpack('!H',frame[16:18])[0];offset=18
                if kind==0x0800 and frame[offset+9]==6:
                    ihl=(frame[offset]&15)*4
                    length=struct.unpack('!H',frame[offset+2:offset+4])[0]
                    # Ignore fragmented packets instead of inventing TCP ports.
                    if struct.unpack('!H',frame[offset+6:offset+8])[0]&0x3fff: raise ValueError()
                    tcp=offset+ihl
                elif kind==0x86dd and frame[offset+6]==6:
                    ihl=40;length=40+struct.unpack('!H',frame[offset+4:offset+6])[0];tcp=offset+40
                else: raise ValueError()
                source,dest=struct.unpack('!HH',frame[tcp:tcp+4]);tcp_length=(frame[tcp+12]>>4)*4
                if ihl<20 or tcp_length<20 or length<ihl+tcp_length: raise ValueError()
                direction='egress' if source==17400 else 'ingress' if dest==17400 else None
                if direction is None: raise ValueError()
                result[direction+'IpBytes']+=length
                result[direction+'TcpPayloadBytes']+=length-ihl-tcp_length
                bucket=buckets.setdefault(second,[second,0,0,0,0,0])
                bucket[1 if direction=='ingress' else 2]+=length
                bucket[3 if direction=='ingress' else 4]+=length-ihl-tcp_length
                bucket[5]+=1
            except (IndexError,ValueError,struct.error): result['unparsedPackets']+=1
        result['captureComplete']=True
    except TimeoutError:
        result['captureComplete']=True
    finally:
        child.terminate()
        try: child.wait(timeout=5)
        except subprocess.TimeoutExpired: child.kill();child.wait()
        child.stdout.close()
        reader.join(timeout=2);child.stderr.close()
    result['kernelDroppedPackets']=capture_stats[-1] if capture_stats else None
    result['captureComplete']=result['captureComplete'] and capture_stats==[0] and result['unparsedPackets']==0
    result['elapsedMs']=int((time.time()-start)*1000)
    result['stoppedByRequest']=bool(stop_file and os.path.exists(stop_file))
    # Compact per-second buckets permit alignment with the phone's test window;
    # omitted seconds have zero captured traffic. No packet contents are retained.
    result['bucketFields']=['utcSecond','ingressIpBytes','egressIpBytes','ingressTcpPayloadBytes','egressTcpPayloadBytes','packets']
    result['buckets']=[buckets[key] for key in sorted(buckets)]
    return result


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seconds',type=int,default=90)
    parser.add_argument('--interface',default='eth0')
    parser.add_argument('--stop-file',help='Optional operator-created marker under /var/lib/linkgate/diagnostics-v02/')
    parser.add_argument('--report-file',help='Write full final counters in the owned diagnostics directory, stdout is a summary')
    args=parser.parse_args()
    if not 5<=args.seconds<=7200 or not args.interface.replace('_','').replace('-','').isalnum():
        parser.error('Use 5..7200 seconds and an explicit interface name')
    for path in [args.stop_file,args.report_file]:
      if path:
        base='/var/lib/linkgate/diagnostics-v02'
        if not os.path.isabs(path) or os.path.commonpath([base,os.path.abspath(path)])!=base:
            parser.error('Diagnostic files must be inside the owned directory')
        directory=os.lstat(os.path.dirname(path))
        if not stat.S_ISDIR(directory.st_mode) or directory.st_uid!=0 or stat.S_IMODE(directory.st_mode)!=0o700:
            parser.error('Diagnostic parent must be a private root-owned directory')
    if args.report_file and os.path.lexists(args.report_file):parser.error('Report already exists')
    result=measure(args.interface,args.seconds,args.stop_file)
    if args.report_file:
        fd=os.open(args.report_file,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
        with os.fdopen(fd,'w') as output:json.dump(result,output)
        result={key:value for key,value in result.items() if key!='buckets'}
        result['reportFile']=args.report_file
    print(json.dumps(result),flush=True)
