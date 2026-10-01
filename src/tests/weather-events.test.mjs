import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';

const enabled = process.env.AIRPORT_WEATHER_INTEGRATION === '1';
const flightsUrl = 'http://localhost:5083';
const weatherUrl = 'http://localhost:5081';
const towerUrl = 'http://localhost:5082';
const daprUrl = process.env.AIRPORT_FLIGHT_DAPR_URL;

async function request(base, path, method = 'GET', body) {
    const response = await fetch(`${base}${path}`, {
        method,
        headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: AbortSignal.timeout(15000),
    });
    const text = await response.text();
    assert(response.ok, `${method} ${path}: ${response.status} ${text}`);
    return text ? JSON.parse(text) : null;
}

async function until(read, accept, description, timeout = 20000) {
    const deadline = Date.now() + timeout;
    let last;
    do {
        last = await read();
        if (accept(last)) return last;
        await delay(250);
    } while (Date.now() < deadline);
    assert.fail(`${description}; last value: ${JSON.stringify(last)}`);
}

test('weather events wake durable flight workflows without service invocation', {
    skip: !enabled,
    timeout: 180000,
}, async t => {
    assert(daprUrl, 'Set AIRPORT_FLIGHT_DAPR_URL to the flight-ops sidecar HTTP URL.');
    assert(['localhost', '127.0.0.1', '[::1]'].includes(new URL(daprUrl).hostname),
        'These opt-in scenarios are only for the local, idle demo.');
    assert.equal((await request(daprUrl, '/v1.0/metadata')).id, 'flight-ops');

    const existing = await request(flightsUrl, '/flights');
    for (const flight of existing.filter(f => f.status < 7)) {
        const workflow = await request(daprUrl, `/v1.0/workflows/dapr/${flight.flightId}`);
        assert(['FAILED', 'COMPLETED', 'TERMINATED', 'CANCELED'].includes(workflow.runtimeStatus),
            `Flight ${flight.flightId} is active. Run these scenarios on an idle demo.`);
    }
    const original = await request(weatherUrl, '/weather/status');
    const created = [];
    const readWeather = () => request(daprUrl, '/v1.0/state/statestore/latest-weather');
    const readFlight = id => request(flightsUrl, `/flights/${id}`);
    const advance = id => request(flightsUrl, `/flights/${id}/advance`, 'POST');
    const publish = snapshot => request(daprUrl, '/v1.0/publish/pubsub/weather-updates', 'POST', snapshot);

    async function terminalState(id, statuses = ['COMPLETED', 'FAILED', 'TERMINATED', 'CANCELED']) {
        // Dapr can return a transient HTTP 500 while a termination checkpoint is being saved.
        // Retain the actual error response if it does not settle within the polling deadline.
        return until(async () => {
            const response = await fetch(`${daprUrl}/v1.0/workflows/dapr/${id}`, {
                signal: AbortSignal.timeout(15000),
            });
            return { ok: response.ok, status: response.status, body: await response.json() };
        }, result => result.ok && statuses.includes(result.body.runtimeStatus), 'Workflow becomes terminal');
    }

    async function preset(name) {
        const snapshot = await request(weatherUrl, `/weather/preset/${name}`, 'POST');
        await until(readWeather, value => value?.observedAt === snapshot.observedAt, 'Snapshot reaches FlightOperations');
        await until(() => request(towerUrl, '/atc/weather'),
            value => value?.observedAt === snapshot.observedAt, 'Snapshot reaches ATC');
        return snapshot;
    }

    async function schedule(suffix, gate) {
        const flight = await request(flightsUrl, '/flights', 'POST', {
            callsign: `EVT${suffix}`, origin: 'AMS', destination: 'LHR',
            aircraftType: 'Airbus A320', gate, scheduledDeparture: new Date().toISOString(),
        });
        created.push(flight.flightId);
        await until(() => readFlight(flight.flightId), value => value.status === 2, 'Flight starts boarding');
        return flight.flightId;
    }

    async function depart(id) {
        await until(() => readFlight(id), value => value.status === 4,
            'Flight receives takeoff clearance', 45000);
    }

    try {
        await request(weatherUrl, '/weather/pause', 'POST');

        await t.test('a weather improvement wakes the hold; duplicate and older events cannot advance it', async () => {
            const fog = await preset('foggy');
            const id = await schedule('HOLD', 'B28');
            await advance(id);
            await until(() => readFlight(id),
                value => value.status === 2 && value.note?.startsWith('Weather hold:'), 'Flight holds in fog');

            for (let i = 0; i < 8; i++) await publish(fog);
            await publish({
                ...fog, observedAt: new Date(Date.parse(fog.observedAt) - 60000).toISOString(),
                condition: 'CAVOK', visibilityMeters: 10000, cloudBaseFeet: 5000, windKnots: 5,
            });
            await delay(1500);
            assert.equal((await readWeather()).condition, fog.condition);
            assert.equal((await request(towerUrl, '/atc/weather')).condition, fog.condition);
            assert.equal((await readFlight(id)).status, 2, 'Notifications do not consume the four hold retries');

            const changedAt = Date.now();
            await preset('cavok');
            await depart(id);
            assert(Date.now() - changedAt < 55000, 'Weather wakes the workflow before its one-minute retry timer');
            assert.equal((await readFlight(id)).note, '', 'The weather-hold note is cleared');

            await advance(id);
            await until(() => readFlight(id), value => value.status === 5, 'Cruise advance is not stolen by an old waiter');
            await advance(id);
            await until(() => readFlight(id), value => value.status === 7, 'Flight lands', 45000);
            await terminalState(id, ['COMPLETED']);
        });

        await t.test('missing weather is a bounded hold and cancellation releases the gate', async () => {
            await request(daprUrl, '/v1.0/state/statestore/latest-weather', 'DELETE');
            const id = await schedule('UNKNOWN', 'B28');
            await advance(id);
            await until(() => readFlight(id),
                value => value.status === 2 && value.note === 'Waiting for the first weather event',
                'Missing weather is not treated as flyable');
            for (let i = 0; i < 4; i++) {
                await advance(id);
                await delay(500);
            }
            await until(() => readFlight(id), value => value.status === 8, 'Hold exhausts its retry budget');
            assert.match((await readFlight(id)).note, /no weather events received/);
        });

        await t.test('flights use weather published before they start, even with the publisher paused', async () => {
            await preset('cavok');
            const id = await schedule('CACHED', 'B28');
            await advance(id);
            await depart(id);
            assert.equal((await request(weatherUrl, '/weather/status')).paused, true);
            await request(flightsUrl, `/flights/${id}/cancel`, 'POST');
            await until(() => readFlight(id), value => value.status === 8, 'Operator cancellation remains available');
        });
    } finally {
        const cleanupErrors = [];
        for (const id of created) {
            try {
                const flight = await readFlight(id);
                if (flight.status !== 7 && flight.status !== 8)
                    await request(flightsUrl, `/flights/${id}/cancel`, 'POST');
                await terminalState(id);
                await request(daprUrl, `/v1.0/workflows/dapr/${id}/purge`, 'POST');
                await request(daprUrl, `/v1.0/actors/AircraftActor/${id}/state`, 'POST',
                    [{ operation: 'delete', request: { key: 'state' } }]);
            } catch (error) {
                cleanupErrors.push(error);
            }
        }

        try {
            let saved = false;
            for (let attempt = 0; attempt < 5 && !saved; attempt++) {
                const response = await fetch(`${daprUrl}/v1.0/state/statestore/aircraft-index`);
                assert(response.ok, 'Read flight index for cleanup');
                const ids = await response.json();
                const save = await fetch(`${daprUrl}/v1.0/state/statestore`, {
                    method: 'POST', headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify([{
                        key: 'aircraft-index', value: ids.filter(id => !created.includes(id)),
                        // Keep the index's normal write mode; the supplied ETag still guards this update.
                        etag: response.headers.get('etag'), options: { concurrency: 'last-write' },
                    }]),
                });
                saved = save.ok;
                if (!saved) assert.equal(save.status, 409, `Unexpected index cleanup failure: ${await save.text()}`);
            }
            assert(saved, 'Remove only the diagnostic flight IDs from the index');

            if (original.overrideActive && original.presetName) {
                await request(weatherUrl, `/weather/preset/${original.presetName}`, 'POST');
            } else {
                await request(weatherUrl, '/weather/override', 'PUT', original.current);
                if (!original.overrideActive) await request(weatherUrl, '/weather/override', 'DELETE');
            }
            if (!original.paused) await request(weatherUrl, '/weather/resume', 'POST');
        } catch (error) {
            cleanupErrors.push(error);
        }
        assert.deepEqual(cleanupErrors.map(error => error.message), [], 'Restore weather controls and remove diagnostic state');
    }
});
