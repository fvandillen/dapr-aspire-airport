export const TAU = Math.PI * 2;
export const CONCOURSES = Object.freeze(['B', 'C', 'D', 'E', 'F', 'G']);
export const RUNWAYS = Object.freeze([
  { names: ['09L', '27R'], z: -260, x: 0, length: 1600, width: 42 },
  { names: ['09R', '27L'], z: -120, x: 0, length: 1600, width: 42 },
]);

export const VIEWS = Object.freeze({
  overview: { position: [1100, 550, 1300], target: [0, 28, 70] },
  terminal: { position: [370, 180, 660], target: [0, 20, 320] },
  runway: { position: [-930, 145, 160], target: [-80, 5, -185] },
  tower: { position: [-480, 132, 302], target: [60, 7, -80] },
});

export function hash(value) {
  let result = 2166136261;
  for (const character of String(value ?? '')) {
    result ^= character.charCodeAt(0);
    result = Math.imul(result, 16777619);
  }
  return result >>> 0;
}

export function seededRandom(seed) {
  let state = hash(seed);
  return () => {
    state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
    return state / 4294967296;
  };
}

export const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
const number = (value, fallback) => Number.isFinite(value) ? value : fallback;

export const panelCameraOffset = width => clamp((number(width, 1000) - 1000) * 0.65, 0, 195);

export function gatePlacement(gate) {
  const match = /^([B-G])\s*0?([1-9]|1\d|2\d)$/i.exec(String(gate ?? '').trim());
  if (match) {
    const concourse = CONCOURSES.indexOf(match[1].toUpperCase());
    const index = Number(match[2]) - 1;
    const side = index % 2 === 0 ? -1 : 1;
    return {
      x: -275 + concourse * 110 + side * 35,
      y: 3.4,
      z: 412 - Math.floor(index / 2) * 21,
      heading: -side * Math.PI / 2,
      known: true,
    };
  }
  const seed = hash(gate);
  return { x: 445 + (seed % 3) * 45, y: 3.4, z: 95 + ((seed >>> 4) % 9) * 35, heading: -Math.PI / 2, known: false };
}

export function runwayFor(name) {
  const normalized = String(name ?? '').toUpperCase().trim();
  return RUNWAYS.find(runway => runway.names.includes(normalized)) ?? null;
}

export function isAirborne(status) {
  return status === 'Cruising' || status === 'AwaitingLandingClearance' || status === 'Departed';
}

export function flightPose(flight, elapsedSeconds = 0) {
  const seed = hash(flight.flightId);
  const gate = gatePlacement(flight.gate);
  const runway = runwayFor(flight.runway) ?? RUNWAYS[seed % RUNWAYS.length];
  const direction = String(flight.runway ?? '').startsWith('27') ? -1 : 1;
  const runwayHeading = direction * Math.PI / 2;
  const elapsed = Math.max(0, number(elapsedSeconds, 0));
  const phase = (seed % 6283) / 1000;
  switch (flight.status) {
    case 'WaitingForGate':
      return { x: -450 - (seed % 3) * 34, y: 3.4, z: 80 + ((seed >>> 4) % 8) * 39, heading: 0 };
    case 'AwaitingTakeoffClearance':
      return { x: -direction * (620 - (seed % 5) * 38), y: 3.4, z: runway.z + 53, heading: runwayHeading };
    case 'Departed': {
      const progress = clamp(elapsed / 42, 0, 1);
      return {
        x: direction * (-650 + progress * 1600),
        y: 3.4 + Math.pow(Math.max(0, (progress - 0.62) / 0.38), 1.15) * 140,
        z: runway.z,
        heading: runwayHeading,
        pitch: progress > 0.62 ? 0.085 : 0,
      };
    }
    case 'Cruising':
    case 'AwaitingLandingClearance': {
      const holding = flight.status === 'AwaitingLandingClearance';
      const angle = phase + elapsed * (holding ? 0.019 : 0.014);
      const radiusX = holding ? 850 : 1120;
      const radiusZ = holding ? 415 : 570;
      return {
        x: Math.cos(angle) * radiusX,
        y: (holding ? 145 : 230) + (seed % 4) * 13,
        z: -40 + Math.sin(angle) * radiusZ,
        heading: Math.atan2(-Math.sin(angle) * radiusX, Math.cos(angle) * radiusZ),
        roll: holding ? -0.08 : -0.045,
      };
    }
    case 'Landed':
      return { x: 445 + (seed % 3) * 45, y: 3.4, z: 95 + ((seed >>> 4) % 9) * 35, heading: -Math.PI / 2 };
    default:
      return { ...gate };
  }
}

export function interpolatePose(current, target, factor) {
  const amount = clamp(number(factor, 1), 0, 1);
  const angle = ((target.heading - current.heading + Math.PI) % TAU + TAU) % TAU - Math.PI;
  return {
    x: current.x + (target.x - current.x) * amount,
    y: current.y + (target.y - current.y) * amount,
    z: current.z + (target.z - current.z) * amount,
    heading: current.heading + angle * amount,
    pitch: (current.pitch ?? 0) + ((target.pitch ?? 0) - (current.pitch ?? 0)) * amount,
    roll: (current.roll ?? 0) + ((target.roll ?? 0) - (current.roll ?? 0)) * amount,
  };
}

export function normalizeFlights(flights) {
  const unique = new Map();
  for (const flight of Array.isArray(flights) ? flights : []) {
    if (flight && typeof flight.flightId === 'string' && flight.flightId.trim()) {
      unique.set(flight.flightId, { ...flight });
    }
  }
  return unique;
}

export function activeClearances(clearances, available, now = Date.now()) {
  if (!available) return [];
  return (Array.isArray(clearances) ? clearances : []).filter(clearance =>
    clearance && (clearance.kind === 'Takeoff' || clearance.kind === 'Landing') &&
    runwayFor(clearance.runway) !== null && Date.parse(clearance.expiresAt) > now,
  );
}

export function weatherAppearance(weather) {
  const condition = String(weather?.condition ?? '').toLowerCase();
  const snow = condition.includes('snow');
  const storm = condition.includes('thunder');
  const rain = condition.includes('rain') || storm;
  const fog = condition.includes('fog');
  const visibility = clamp(number(weather?.visibilityMeters, 18000), 150, 25000);
  return {
    kind: snow ? 'snow' : rain ? 'rain' : 'none',
    storm,
    wetness: snow ? 0.42 : storm ? 1 : rain ? 0.75 : 0,
    cloudiness: storm ? 0.96 : fog ? 0.86 : snow ? 0.82 : condition.includes('overcast') ? 0.85
      : condition.includes('broken') ? 0.68 : rain ? 0.58 : condition.includes('scattered') ? 0.32 : 0.12,
    fogDensity: !weather ? 0.00012 : clamp(0.95 / visibility, 0.00008, 0.0015),
    wind: clamp(number(weather?.windKnots, 0), 0, 70),
    windDirection: number(weather?.windDirectionDegrees, 0) * Math.PI / 180,
  };
}
