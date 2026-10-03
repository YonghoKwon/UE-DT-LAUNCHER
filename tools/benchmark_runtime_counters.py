"""Optional, bounded .NET profiling. Private raw CSV is never a CI artifact."""
import csv
import json
import os
from pathlib import Path
import subprocess

FIELDS = ('cpu-usage', 'alloc-rate', 'gc-heap-size', 'gen-0-gc-count', 'gen-1-gc-count',
          'gen-2-gc-count', 'time-in-gc', 'threadpool-thread-count', 'monitor-lock-contention-count')


class Counters:
    def __init__(self, tool, pid, root):
        self.output = root / 'runtime-counters.csv'
        self.log = (root / 'runtime-counters-private.log').open('w', encoding='utf-8')
        self.process = subprocess.Popen([str(Path(tool).resolve(strict=True)), 'collect', '-p', str(pid),
            '--counters', 'EventCounters\\System.Runtime[' + ','.join(FIELDS) + '],UeDtLauncher.Distribution',
            '--format', 'csv', '--refresh-interval', '1', '--maxTimeSeries', '64', '--maxHistograms', '8',
            '--duration', '00:00:00:20', '--output', str(self.output)], stdout=self.log, stderr=self.log,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)

    def finish(self):
        try:
            code = self.process.wait(timeout=30)
        except subprocess.TimeoutExpired:
            self.process.kill(); self.process.wait(); code = None
        finally:
            self.log.close()
        return {'source': 'dotnet-counters', 'refreshSeconds': 1, 'durationSeconds': 20,
                'exitCode': code, 'available': self.output.exists() and self.output.stat().st_size > 0,
                'missingReason': None if code == 0 else 'profiler-failed-or-timeout',
                'changesTiming': True}
