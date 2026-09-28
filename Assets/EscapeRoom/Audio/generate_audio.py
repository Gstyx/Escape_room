"""Gera os clipes de audio do Escape Room (16-bit PCM mono 44.1kHz).

Nao depende de numpy: usa apenas a stdlib. Todos os loops de ambiente usam
frequencias que fecham um numero inteiro de ciclos no tempo do loop, para
nao estalar na repeticao.
"""
import math
import os
import random
import struct
import wave

SR = 44100
OUT = os.path.dirname(os.path.abspath(__file__))
os.makedirs(OUT, exist_ok=True)
random.seed(7)


def write(name, samples, peak=0.85):
    hi = max(1e-9, max(abs(s) for s in samples))
    scale = peak / hi
    # fade de 3ms nas pontas para nao estalar
    n = len(samples)
    fade = min(132, n // 4)
    data = bytearray()
    for i, s in enumerate(samples):
        v = s * scale
        if i < fade:
            v *= i / fade
        if i > n - fade:
            v *= (n - 1 - i) / fade
        v = max(-1.0, min(1.0, v))
        data += struct.pack("<h", int(v * 32767))
    with wave.open(os.path.join(OUT, name), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(bytes(data))
    print("  %-22s %5.2fs" % (name, n / SR))


def env_ad(i, n, attack=0.01, release=0.3):
    a = int(SR * attack) or 1
    r = int(SR * release) or 1
    if i < a:
        return i / a
    if i > n - r:
        return max(0.0, (n - i) / r)
    return 1.0


def sine(f, t):
    return math.sin(2 * math.pi * f * t)


def square(f, t, duty=0.5):
    return 1.0 if (f * t) % 1.0 < duty else -1.0


def saw(f, t):
    return 2.0 * ((f * t) % 1.0) - 1.0


# ---------------------------------------------------------------- ambient pad
def ambient_pad():
    """Drone imersivo de 12s, sem costura. Harmony: 55 / 82.5 / 110 / 165 / 220."""
    dur = 12.0
    n = int(SR * dur)
    # LFO lento de amplitude, tambem com periodo divisor de 12s
    lfo = [0.5, 0.25, 0.1667, 0.125, 0.0833]
    parts = [(55, 0.55, 0), (82.5, 0.40, 1), (110, 0.30, 2), (165, 0.16, 0), (220, 0.10, 3)]
    out = []
    for i in range(n):
        t = i / SR
        v = 0.0
        for f, amp, li in parts:
            mod = 0.65 + 0.35 * sine(1.0 / lfo[li], t)
            v += amp * mod * sine(f, t)
        # leve ruido filtrado (ar-condicionado / ventilacao)
        v += 0.035 * random.uniform(-1, 1)
        out.append(v * 0.5)
    return out


# ------------------------------------------------------------------- acoes
def ui_click():
    n = int(SR * 0.07)
    out = []
    for i in range(n):
        t = i / SR
        e = math.exp(-t * 70)
        out.append((0.5 * random.uniform(-1, 1) + 0.6 * sine(1500, t)) * e)
    return out


def power_up():
    """Sequencia de bipes ascendentes: o terminal 'acorde'."""
    dur = 1.5
    n = int(SR * dur)
    steps = [(0.00, 220), (0.18, 330), (0.36, 440), (0.54, 660), (0.78, 880)]
    out = [0.0] * n
    for start, f in steps:
        s0 = int(SR * start)
        ln = int(SR * 0.30)
        for i in range(ln):
            if s0 + i >= n:
                break
            t = i / SR
            e = math.exp(-t * 9) * env_ad(i, ln, 0.005, 0.05)
            v = 0.55 * square(f, t, 0.35) + 0.45 * sine(f * 2, t)
            out[s0 + i] += v * e * 0.5
    return out


def error_buzz():
    dur = 0.55
    n = int(SR * dur)
    out = []
    for i in range(n):
        t = i / SR
        am = 0.6 + 0.4 * sine(28, t)          # tremulo agressivo
        e = env_ad(i, n, 0.01, 0.12)
        v = 0.6 * square(104, t, 0.42) + 0.3 * saw(156, t) + 0.15 * random.uniform(-1, 1)
        out.append(v * am * e)
    return out


def unlock_chime():
    dur = 1.4
    n = int(SR * dur)
    out = [0.0] * n
    for start, f in [(0.0, 659.25), (0.16, 987.77), (0.34, 1318.5)]:
        s0 = int(SR * start)
        ln = n - s0
        for i in range(ln):
            t = i / SR
            e = math.exp(-t * 4.2)
            v = 0.5 * sine(f, t) + 0.22 * sine(f * 2, t) + 0.1 * sine(f * 3.01, t)
            out[s0 + i] += v * e * 0.6
    return out


def servo_door():
    """Porta blindada: rumble grave + ruido, com o estalo final das travas."""
    dur = 3.0
    n = int(SR * dur)
    out = []
    lp = 0.0
    for i in range(n):
        t = i / SR
        # varredura grave 55 -> 38 Hz
        f = 55 - 17 * min(1.0, t / dur)
        body = 0.7 * sine(f, t) + 0.3 * sine(f * 1.5, t)
        # ruido passa-baixa de 1 polo
        lp += 0.06 * (random.uniform(-1, 1) - lp)
        noise = lp * 2.2 * (1.0 - 0.45 * t / dur)
        e = env_ad(i, n, 0.08, 0.35)
        out.append((body + noise) * e * 0.6)
    # estalo no fim
    clank = int(SR * (dur - 0.18))
    for i in range(clank, n):
        t = (i - clank) / SR
        out[i] += (0.5 * sine(180, t) + 0.5 * random.uniform(-1, 1)) * math.exp(-t * 45) * 0.7
    return out


def success_stinger():
    dur = 2.6
    n = int(SR * dur)
    out = [0.0] * n
    arp = [523.25, 659.25, 783.99, 1046.5, 1318.5]
    for k, f in enumerate(arp):
        s0 = int(SR * (0.10 * k))
        ln = n - s0
        for i in range(ln):
            t = i / SR
            e = math.exp(-t * 2.1)
            v = 0.5 * sine(f, t) + 0.25 * sine(f * 2, t) + 0.12 * sine(f * 3, t)
            out[s0 + i] += v * e * 0.45
    # pad de sustentacao
    for i in range(n):
        t = i / SR
        out[i] += 0.10 * (sine(130.81, t) + sine(196.0, t)) * (1 - t / dur)
    return out


def alarm_beep():
    dur = 0.22
    n = int(SR * dur)
    out = []
    for i in range(n):
        t = i / SR
        e = env_ad(i, n, 0.004, 0.06)
        out.append((0.6 * square(932, t, 0.5) + 0.4 * sine(932, t)) * e)
    return out


def pickup():
    dur = 0.22
    n = int(SR * dur)
    out = []
    for i in range(n):
        t = i / SR
        e = math.exp(-t * 22)
        out.append((0.45 * sine(1420, t) + 0.3 * sine(2130, t) + 0.25 * random.uniform(-1, 1)) * e)
    return out


def lock_insert():
    """Encaixe: thunk grave + clique metalico."""
    dur = 0.38
    n = int(SR * dur)
    out = []
    for i in range(n):
        t = i / SR
        thunk = 0.8 * sine(88, t) * math.exp(-t * 11)
        click = (0.4 * random.uniform(-1, 1) + 0.4 * sine(2400, t)) * math.exp(-t * 60)
        out.append((thunk + click) * 0.7)
    return out


def dispense():
    """O terminal cospe o cartao: motorzinho + chiado + chipe."""
    dur = 0.9
    n = int(SR * dur)
    out = []
    lp = 0.0
    for i in range(n):
        t = i / SR
        lp += 0.25 * (random.uniform(-1, 1) - lp)
        motor = 0.35 * square(70 + 40 * t, t, 0.5) * (1 - t / dur)
        e = env_ad(i, n, 0.02, 0.2)
        out.append((motor + lp * 0.5) * e * 0.7)
    chip = int(SR * (dur - 0.25))
    for i in range(chip, n):
        t = (i - chip) / SR
        out[i] += 0.6 * (sine(1046, t) + 0.5 * sine(1568, t)) * math.exp(-t * 16)
    return out


if __name__ == "__main__":
    print("Gerando audio em %s" % OUT)
    write("ambient_pad.wav", ambient_pad(), 0.55)
    write("ui_click.wav", ui_click(), 0.6)
    write("power_up.wav", power_up(), 0.7)
    write("error_buzz.wav", error_buzz(), 0.65)
    write("unlock_chime.wav", unlock_chime(), 0.7)
    write("servo_door.wav", servo_door(), 0.8)
    write("success_stinger.wav", success_stinger(), 0.75)
    write("alarm_beep.wav", alarm_beep(), 0.5)
    write("pickup.wav", pickup(), 0.6)
    write("lock_insert.wav", lock_insert(), 0.75)
    write("dispense.wav", dispense(), 0.7)
    print("OK")
