"""Sample an owned process without inventing zero values for missing metrics."""
import threading


class ProcessSampler:
    def __init__(self, pid):
        self.pid = pid
        self.peak = self.cpu = None
        self.samples = 0
        self.reason = None
        self.stop = threading.Event()
        self.thread = None

    def sample(self):
        try:
            import psutil
            process = psutil.Process(self.pid)
            memory = process.memory_info()
            value = getattr(memory, "peak_wset", memory.rss)
            self.peak = max(self.peak or 0, value) if value > 0 else self.peak
            cpu = process.cpu_times()
            self.cpu = max(self.cpu or 0, cpu.user + cpu.system)
            self.samples += 1
        except ImportError:
            self.reason = "psutil-unavailable"
        except Exception as error:
            # Only exception type, never an error message containing a host path.
            self.reason = type(error).__name__

    def __enter__(self):
        self.sample()
        def poll():
            while not self.stop.wait(.02):
                self.sample()
        self.thread = threading.Thread(target=poll, daemon=True)
        self.thread.start()
        return self

    def __exit__(self, *_):
        self.stop.set()
        self.thread.join()
        self.sample()

    def result(self):
        return {"cpu_seconds": self.cpu, "peak_rss_bytes": self.peak,
                "source": "psutil-process-sampled", "sample_interval_ms": 20,
                "samples": self.samples, "missing_reason": self.reason if not self.samples else "peak-rss-unavailable" if self.peak is None else None}
