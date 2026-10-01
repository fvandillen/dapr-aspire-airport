import test from 'node:test';
import assert from 'node:assert/strict';
import {
  CONCOURSES, activeClearances, flightPose, gatePlacement, interpolatePose,
  normalizeFlights, panelCameraOffset, runwayFor, seededRandom, weatherAppearance,
} from './scene-model.js';

test('the complete gate catalog has distinct finite stands and stable arbitrary-gate fallbacks', () => {
  const stands = new Set();
  for (const concourse of CONCOURSES) {
    for (let number = 1; number <= 29; number++) {
      const pose = gatePlacement(`${concourse}${number}`);
      assert.equal(pose.known, true);
      assert.ok(Object.values(pose).every(value => typeof value !== 'number' || Number.isFinite(value)));
      stands.add(`${pose.x},${pose.z}`);
    }
  }
  assert.equal(stands.size, 174);
  for (const name of ['B0', 'B30', 'A2', '', null, '<script>bad</script>', 'C99999999999999']) {
    assert.equal(gatePlacement(name).known, false);
    assert.deepEqual(gatePlacement(name), gatePlacement(name));
  }
  assert.deepEqual(gatePlacement(' c 01 '), gatePlacement('C1'));
});

test('workflow stages have distinct waiting, stand, hold-short, and airborne placements', () => {
  const flight = { flightId: 'test', gate: 'B4', runway: '09L' };
  const parked = flightPose({ ...flight, status: 'BoardingPushback' });
  assert.deepEqual(parked, flightPose({ ...flight, status: 'Scheduled' }));
  const wait = flightPose({ ...flight, status: 'WaitingForGate' });
  assert.notEqual(wait.x, parked.x);
  assert.notEqual(flightPose({ ...flight, status: 'AwaitingTakeoffClearance' }).z, -260);
  for (const status of ['Cruising', 'AwaitingLandingClearance']) {
    assert.ok(flightPose({ ...flight, status }).y > 100);
  }
  const cancelled = { ...flight, status: 'Cancelled' };
  assert.deepEqual(flightPose(cancelled, 0), flightPose(cancelled, 999));
});

test('departures respect runway direction and stop at the illustrated end of their stage', () => {
  const base = { flightId: 'flight', status: 'Departed', gate: 'B1' };
  assert.ok(flightPose({ ...base, runway: '09L' }, 30).x > flightPose({ ...base, runway: '09L' }, 0).x);
  assert.ok(flightPose({ ...base, runway: '27R' }, 30).x < flightPose({ ...base, runway: '27R' }, 0).x);
  assert.deepEqual(flightPose(base, 100), flightPose(base, 1000));
  assert.deepEqual(flightPose(base, -1), flightPose(base, 0));
});

test('headings interpolate across the shortest angle, including wrap-around', () => {
  const from = { x: 0, y: 0, z: 0, heading: Math.PI - 0.1 };
  const to = { x: 10, y: 20, z: 30, heading: -Math.PI + 0.1 };
  const result = interpolatePose(from, to, 0.5);
  assert.ok(Math.abs(result.heading - Math.PI) < 0.0001);
  assert.equal(result.x, 5);
});

test('snapshots de-duplicate stable IDs and do not invent flights', () => {
  assert.equal(normalizeFlights(null).size, 0);
  assert.equal(normalizeFlights([]).size, 0);
  const flights = normalizeFlights([null, {}, { flightId: '' }, { flightId: 'one', status: 'Scheduled' }, { flightId: 'one', status: 'Cancelled' }]);
  assert.equal(flights.size, 1);
  assert.equal(flights.get('one').status, 'Cancelled');
});

test('reciprocal runway designations share physical strips; invalid or expired data never clears a runway', () => {
  assert.equal(runwayFor('09L'), runwayFor('27R'));
  assert.equal(runwayFor('09R'), runwayFor('27L'));
  assert.equal(runwayFor('unknown'), null);
  const now = Date.parse('2026-09-18T12:00:00Z');
  const valid = { runway: '09L', kind: 'Takeoff', expiresAt: '2026-09-18T12:00:15Z' };
  const clearances = [valid, { ...valid, expiresAt: 'broken' }, { ...valid, runway: 'unknown' }, { ...valid, kind: 'bad' }];
  assert.deepEqual(activeClearances(clearances, true, now), [valid]);
  assert.deepEqual(activeClearances(clearances, false, now), []);
  assert.deepEqual(activeClearances(clearances, true, now + 15000), []);
});

test('service weather presets map to restrained local effects', () => {
  assert.equal(weatherAppearance({ condition: 'CAVOK' }).kind, 'none');
  assert.equal(weatherAppearance({ condition: 'Light rain' }).kind, 'rain');
  assert.equal(weatherAppearance({ condition: 'Thunderstorms' }).storm, true);
  assert.equal(weatherAppearance({ condition: 'Snow' }).kind, 'snow');
  assert.ok(weatherAppearance({ condition: 'Fog', visibilityMeters: 600 }).fogDensity >
    weatherAppearance({ condition: 'CAVOK', visibilityMeters: 10000 }).fogDensity);
  assert.equal(weatherAppearance(null).wetness, 0);
  assert.equal(weatherAppearance({ windKnots: 900 }).wind, 70);
  assert.ok(Number.isFinite(weatherAppearance({ visibilityMeters: NaN }).fogDensity));
});

test('scenery random sequences are deterministic', () => {
  const a = seededRandom('airport');
  const b = seededRandom('airport');
  for (let index = 0; index < 20; index++) assert.equal(a(), b());
});

test('camera framing reserves desktop panel space without shifting narrow screens', () => {
  assert.equal(panelCameraOffset(800), 0);
  assert.equal(panelCameraOffset(1000), 0);
  assert.equal(panelCameraOffset(1200), 130);
  assert.equal(panelCameraOffset(1440), 195);
  assert.equal(panelCameraOffset(4000), 195);
  assert.equal(panelCameraOffset(NaN), 0);
});
