from collections import deque


class BounceDetector:
    def __init__(self, window: int = 5, min_drop: float = 0.010,
                 cooldown: int = 12):
        self._zs                  = deque(maxlen=window)
        self._min_drop            = min_drop
        self._cooldown            = cooldown
        self._frames_since_bounce = cooldown

    def update(self, z: float) -> bool:
        self._zs.append(z)
        self._frames_since_bounce += 1

        if (len(self._zs) < self._zs.maxlen or
                self._frames_since_bounce < self._cooldown):
            return False

        zs  = list(self._zs)
        mid = len(zs) // 2

        is_local_max = (zs[mid] == max(zs) and
                        zs[mid] > zs[mid - 1] and
                        zs[mid] > zs[mid + 1])

        if not is_local_max:
            return False

        variation = zs[mid] - min(zs)
        if variation < self._min_drop:
            return False

        self._frames_since_bounce = 0
        return True

    def reset(self):
        self._zs.clear()
        self._frames_since_bounce = self._cooldown