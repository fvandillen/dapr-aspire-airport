import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';

const enabled = process.env.AIRPORT_RESET_INTEGRATION === '1';
const flightsUrl = process.env.AIRPORT_FLIGHTS_URL ?? 'http://localhost:5083';
const towerUrl = process.env.AIRPORT_TOWER_URL ?? 'http://localhost:5082';
const daprUrl = process.env.AIRPORT_FLIGHT_DAPR_URL;
const towerDaprUrl = process.env.AIRPORT_TOWER_DAPR_URL;

async function request(base, path, method = 'GET', body) {
    const response = await fetch(`${base}${path}`, {
        method,
        headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: AbortSignal.timeout(100000),
    });
    const text = await response.text();
    assert(response.ok, `${method} ${path}: ${response.status} ${text}`);
    return text ? JSON.parse(text) : null;
}

async function until(read, accept, description) {
    const deadline = Date.now() + 30000;
    let last;
    do {
        last = await read();
        if (accept(last)) return last;
        await delay(200);
    } while (Date.now() < deadline);
    assert.fail(`${description}; last value: ${JSON.stringify(last)}`);
}

test('airport reset removes active and terminal flights without flushing unrelated state', {
    skip: !enabled,
    timeout: 180000,
}, async () => {
    assert(daprUrl && towerDaprUrl, 'Set both airport sidecar URLs; use an isolated, empty local demo.');
    for (const url of [flightsUrl, towerUrl, daprUrl, towerDaprUrl]) {
        assert(['localhost', '127.0.0.1', '[::1]'].includes(new URL(url).hostname),
            'Reset scenarios are only for an isolated local demo.');
    }
    assert.equal((await request(daprUrl, '/v1.0/metadata')).id, 'flight-ops');
    assert.equal((await request(towerDaprUrl, '/v1.0/metadata')).id, 'atc-service');
    assert.deepEqual(await request(flightsUrl, '/flights'), [], 'Start with an empty airport; reset is destructive.');
    assert.deepEqual(await request(towerUrl, '/clearances'), [], 'Start without existing clearances.');

    const sentinel = `reset-test-${crypto.randomUUID()}`;
    const sentinelValue = { retained: true };
    const created = [];
    try {
        for (const base of [daprUrl, towerDaprUrl])
            await request(base, '/v1.0/state/statestore', 'POST', [{ key: sentinel, value: sentinelValue }]);

        const weather = {
            observedAt: new Date().toISOString(), condition: 'CAVOK',
            temperatureCelsius: 15, windKnots: 5, windDirectionDegrees: 270,
            visibilityMeters: 10000, cloudBaseFeet: 5000,
        };
        await request(flightsUrl, '/flight-ops/weather-updates', 'POST', weather);
        await request(towerUrl, '/atc/weather-updates', 'POST', weather);

        for (const callsign of ['RESET-ACTIVE', 'RESET-WAITING', 'RESET-CANCELLED']) {
            const flight = await request(flightsUrl, '/flights', 'POST', {
                callsign, origin: 'AMS', destination: 'LHR', aircraftType: 'Airbus A320',
                gate: callsign === 'RESET-CANCELLED' ? 'D13' : 'D12',
                scheduledDeparture: new Date().toISOString(),
            });
            created.push(flight.flightId);
        }
        await until(() => request(flightsUrl, `/flights/${created[0]}`),
            value => value.status === 2, 'First flight holds D12');
        await until(() => request(flightsUrl, `/flights/${created[1]}`),
            value => value.status === 1, 'Second flight waits for D12');
        await request(flightsUrl, `/flights/${created[2]}/cancel`, 'POST');

        const clearance = {
            flightId: created[0], callsign: 'RESET-ACTIVE', kind: 0,
            requestedAt: new Date().toISOString(),
        };
        await request(towerUrl, '/atc/clearance-requests', 'POST', clearance);
        assert.equal((await request(towerUrl, '/clearances')).length, 1);
        assert(await request(daprUrl, '/v1.0/state/statestore/latest-weather'));

        await request(flightsUrl, '/flights', 'DELETE');
        assert.deepEqual(await request(flightsUrl, '/flights'), []);
        assert.deepEqual(await request(towerUrl, '/clearances'), []);
        assert.equal(await request(towerUrl, '/atc/weather'), null);
        assert.equal(await request(daprUrl, '/v1.0/state/statestore/latest-weather'), null);
        assert.equal(await request(daprUrl, '/v1.0/state/statestore/aircraft-index'), null);
        const resetCheckpoint = await request(towerDaprUrl, '/v1.0/state/statestore/last-airport-reset');
        assert(resetCheckpoint, 'ATC durably remembers the reset cutoff for late message delivery');

        for (const id of created) {
            const response = await fetch(`${flightsUrl}/flights/${id}`);
            assert.equal(response.status, 404, 'Deleted flights cannot appear as empty ghost actors');
            for (const command of ['advance', 'cancel', 'clearance']) {
                const response = await fetch(`${flightsUrl}/flights/${id}/${command}`, {
                    method: 'POST', headers: { 'Content-Type': 'application/json' },
                    body: command === 'clearance' ? JSON.stringify({ kind: 0, granted: true }) : undefined,
                });
                assert.equal(response.status, 404, 'Deleted flights cannot be controlled');
            }
            const actor = await fetch(`${daprUrl}/v1.0/actors/AircraftActor/${id}/state/state`);
            assert([204, 404].includes(actor.status), `Aircraft ${id} state was deleted: ${actor.status}`);
            const workflow = await fetch(`${daprUrl}/v1.0/workflows/dapr/${id}`);
            assert.equal(workflow.status, 404, 'Workflow history was purged');
        }

        // Simulate messages delivered after reset: they must be acknowledged without rewriting state.
        await request(towerUrl, '/atc/clearance-requests', 'POST', clearance);
        await request(flightsUrl, '/flight-ops/clearance-results', 'POST', {
            ...clearance, granted: true, runway: '09L', reason: 'Late result',
            decidedAt: new Date().toISOString(),
        });
        assert.deepEqual(await request(towerUrl, '/clearances'), []);
        await request(flightsUrl, '/flights', 'DELETE');
        assert.deepEqual(await request(flightsUrl, '/flights'), [], 'Reset is safe on an already empty airport');
        const latestResetCheckpoint = await request(towerDaprUrl, '/v1.0/state/statestore/last-airport-reset');

        for (const base of [daprUrl, towerDaprUrl])
            assert.deepEqual(await request(base, `/v1.0/state/statestore/${sentinel}`), sentinelValue);

        const fresh = await request(flightsUrl, '/flights', 'POST', {
            callsign: 'RESET-FRESH', origin: 'AMS', destination: 'LHR', aircraftType: 'Airbus A320',
            gate: 'D12', scheduledDeparture: new Date().toISOString(),
        });
        await until(() => request(flightsUrl, `/flights/${fresh.flightId}`),
            value => value.status === 2, 'A new flight can immediately use the released D12 gate');
        await request(flightsUrl, '/flight-ops/weather-updates', 'POST', {
            ...weather, observedAt: new Date().toISOString(),
        });
        await request(flightsUrl, `/flights/${fresh.flightId}/advance`, 'POST');
        await until(() => request(flightsUrl, `/flights/${fresh.flightId}`),
            value => value.status === 4, 'Pub/sub takeoff decision raises the workflow clearance event');
        assert.equal((await request(towerUrl, '/clearances'))[0].runway, '09L',
            'Reset frees the first runway rather than waiting for reservation expiry');

        await request(towerUrl, '/atc/airport-reset', 'POST', resetCheckpoint);
        await request(towerUrl, '/atc/airport-reset', 'POST', latestResetCheckpoint);
        await request(flightsUrl, '/flight-ops/airport-reset-completed', 'POST', {
            workflowId: latestResetCheckpoint.workflowId ?? latestResetCheckpoint.WorkflowId,
        });
        assert.equal((await request(towerUrl, '/clearances')).length, 1,
            'Duplicate and older reset messages must not delete new clearances');
        await request(flightsUrl, `/flights/${fresh.flightId}/advance`, 'POST');
        await until(() => request(flightsUrl, `/flights/${fresh.flightId}`),
            value => value.status === 5, 'Flight enters cruise');
        await request(flightsUrl, `/flights/${fresh.flightId}/advance`, 'POST');
        await until(() => request(flightsUrl, `/flights/${fresh.flightId}`),
            value => value.status === 7, 'Pub/sub landing decision raises the workflow clearance event');

        // A workflow can exist without an index entry if its scheduling/index write was interrupted.
        const orphanId = `FL-ORPHAN-${crypto.randomUUID()}`;
        await request(daprUrl, `/v1.0/workflows/dapr/FlightWorkflow/start?instanceID=${orphanId}`, 'POST', {
            flightId: orphanId, callsign: 'RESET-ORPHAN', origin: 'AMS', destination: 'LHR',
            aircraftType: 'Airbus A320', gate: 'D14',
        });
        assert(!(await request(flightsUrl, '/flights')).some(flight => flight.flightId === orphanId));
        await request(flightsUrl, '/flights', 'DELETE');
        assert.equal((await fetch(`${daprUrl}/v1.0/workflows/dapr/${orphanId}`)).status, 404,
            'Reset discovers and purges workflows missing from the flight index');

        const pending = await Promise.all(Array.from({ length: 6 }, (_, index) =>
            request(flightsUrl, '/flights', 'POST', {
                callsign: `RESET-PENDING-${index}`, origin: 'AMS', destination: 'LHR',
                aircraftType: 'Airbus A320', gate: 'D12',
                scheduledDeparture: new Date().toISOString(),
            })));
        await request(flightsUrl, '/flights', 'DELETE');
        await delay(1000);
        assert.deepEqual(await request(flightsUrl, '/flights'), [], 'Queued activities cannot restore flights');
        for (const flight of pending) {
            const actor = await fetch(`${daprUrl}/v1.0/actors/AircraftActor/${flight.flightId}/state/state`);
            assert([204, 404].includes(actor.status), 'Queued initialization cannot restore actor state');
            assert.equal((await fetch(`${daprUrl}/v1.0/workflows/dapr/${flight.flightId}`)).status, 404);
        }
    } finally {
        await request(flightsUrl, '/flights', 'DELETE');
        for (const base of [daprUrl, towerDaprUrl])
            await request(base, `/v1.0/state/statestore/${sentinel}`, 'DELETE');
    }
});
