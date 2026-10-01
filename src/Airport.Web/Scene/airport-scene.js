import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js';
import { createAircraftFactory } from './aircraft.js';
import { Resources, createScenery } from './scenery.js';
import {
  VIEWS, activeClearances, clamp, flightPose, hash, interpolatePose, isAirborne,
  normalizeFlights, panelCameraOffset, seededRandom, weatherAppearance,
} from './scene-model.js';

const PALETTES = {
  dusk: {
    sky: '#142d50', horizon: '#879caf', ground: '#425b70',
    sun: '#ffd5a2', sunIntensity: 1.8, ambient: 1.1, exposure: 0.98, lamps: 1,
    sunPosition: [-1100, 390, -750],
  },
  day: {
    sky: '#2c6f9d', horizon: '#c9e1e8', ground: '#7eabb9',
    sun: '#fff3dc', sunIntensity: 3.2, ambient: 1.8, exposure: 0.95, lamps: 0.3,
    sunPosition: [-450, 1400, 280],
  },
  night: {
    sky: '#030c20', horizon: '#19324e', ground: '#112035',
    sun: '#94b8f1', sunIntensity: 0.42, ambient: 0.6, exposure: 1.05, lamps: 1.35,
    sunPosition: [-700, 950, -500],
  },
};

export function createScene(hostElement, dotNetReference) {
  if (!(hostElement instanceof HTMLElement)) throw new Error('The airport scene needs an attached HTML host element.');
  const resources = new Resources();
  const scene = new THREE.Scene();
  const planes = new Map();
  const listeners = [];
  const landmarks = [];
  const disposables = [];
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  const canvas = document.createElement('canvas');
  const labelLayer = document.createElement('div');
  const camera = new THREE.PerspectiveCamera(47, 1, 4, 18000);
  const raycaster = new THREE.Raycaster();
  const pointer = new THREE.Vector2();
  const projected = new THREE.Vector3();
  const sphere = new THREE.Spherical();
  const cameraOffset = new THREE.Vector3();
  const followOffset = new THREE.Vector3(80, 54, 96);
  let renderer;
  let context;
  let controls;
  let composer;
  let bloom;
  let sun;
  let hemisphere;
  let scenery;
  let observer;
  let rain;
  let snow;
  let environmentTarget;
  let disposed = false;
  let lost = false;
  let ready = false;
  let raf = 0;
  let expiryTimer = 0;
  let lastFrame = 0;
  let width = 1;
  let height = 1;
  let elapsed = 0;
  let motion = !reducedMotion.matches;
  let motionChosen = false;
  let labelsEnabled = true;
  let timeOfDay = 'dusk';
  let quality = 'high';
  let selectedId = null;
  let followingId = null;
  let cameraTransition = null;
  let weather = null;
  let weatherStyle = weatherAppearance(null);
  let snapshot = { flights: [], clearances: [], flightsAvailable: false, weatherAvailable: false, clearancesAvailable: false };
  let clearanceSignature = '';
  let pointerStart = null;
  const pointers = new Set();
  const random = seededRandom('airport-weather');

  function listen(target, name, handler, options) {
    target.addEventListener(name, handler, options);
    listeners.push(() => target.removeEventListener(name, handler, options));
  }

  function notify(method, ...args) {
    try {
      Promise.resolve(dotNetReference.invokeMethodAsync(method, ...args)).catch(error => {
        console.warn(`Airport scene ${method} callback failed; the component may have been disposed.`, error);
      });
    } catch (error) {
      console.warn(`Airport scene ${method} callback failed; the component may have been disposed.`, error);
    }
  }

  function invalidate() {
    if (!disposed && ready && !document.hidden && !raf) raf = requestAnimationFrame(frame);
  }

  function applyPose(plane) {
    const pose = plane.pose;
    plane.group.position.set(pose.x, pose.y - 3.4 * (1 - plane.size) * (1 - clamp((pose.y - 3.4) / 12, 0, 1)), pose.z);
    plane.group.rotation.set(-(pose.pitch ?? 0), pose.heading, pose.roll ?? 0, 'YXZ');
    plane.gear.visible = pose.y < 12;
    plane.selection.position.set(pose.x, pose.y > 12 ? pose.y - 5 : 0.35, pose.z);
    plane.selection.visible = plane.flight.flightId === selectedId;
  }

  function createLabel(flight) {
    const element = document.createElement('button');
    element.type = 'button';
    element.className = 'aircraft-label';
    element.style.cssText = 'position:absolute;left:0;top:0;pointer-events:auto;white-space:nowrap;';
    const callsign = document.createElement('span');
    const status = document.createElement('small');
    element.append(callsign, status);
    const handler = event => {
      event.stopPropagation();
      selectFlight(flight.flightId);
    };
    element.addEventListener('click', handler);
    labelLayer.append(element);
    return { element, callsign, status, remove: () => { element.removeEventListener('click', handler); element.remove(); } };
  }

  function updateLabel(plane) {
    const flight = plane.flight;
    const status = String(flight.status ?? 'Unknown').replace(/([a-z])([A-Z])/g, '$1 $2');
    plane.label.callsign.textContent = flight.callsign || flight.flightId.slice(0, 8);
    plane.label.status.textContent = `${status}${!snapshot.flightsAvailable ? ' · stale' : ''}`;
    plane.label.element.title = `${flight.origin || '—'} → ${flight.destination || '—'} · ${flight.aircraftType || 'Aircraft'} · Gate ${flight.gate || '—'}`;
    plane.label.element.setAttribute('aria-label', `Select flight ${flight.callsign || flight.flightId}, ${status}${!snapshot.flightsAvailable ? ', stale data' : ''}`);
    plane.label.element.setAttribute('aria-pressed', String(flight.flightId === selectedId));
    plane.label.element.classList.toggle('is-selected', flight.flightId === selectedId);
    plane.label.element.classList.toggle('is-airborne', isAirborne(flight.status));
  }

  function selectFlight(id) {
    api.focusFlight(id);
    notify('SelectFlight', id);
  }

  function updateLabels() {
    camera.updateMatrixWorld();
    const occupied = [];
    const ordered = [...planes.values()].sort((a, b) =>
      Number(b.flight.flightId === selectedId) - Number(a.flight.flightId === selectedId) ||
      a.group.position.distanceToSquared(camera.position) - b.group.position.distanceToSquared(camera.position),
    );
    for (const plane of ordered) {
      const element = plane.label.element;
      if (!labelsEnabled) { element.hidden = true; element.style.display = 'none'; continue; }
      projected.copy(plane.group.position);
      projected.y += 12;
      projected.project(camera);
      const x = (projected.x * 0.5 + 0.5) * width;
      const y = (-projected.y * 0.5 + 0.5) * height;
      const outside = projected.z < -1 || projected.z > 1 || x < 35 || x > width - 35 || y < 42 || y > height;
      const overlaps = occupied.some(([px, py]) => Math.abs(x - px) < 115 && Math.abs(y - py) < 39);
      element.hidden = outside || overlaps;
      element.style.display = element.hidden ? 'none' : '';
      if (!element.hidden) {
        occupied.push([x, y]);
        element.style.transform = `translate3d(${x.toFixed(1)}px,${y.toFixed(1)}px,0) translate(-50%,-100%)`;
      }
    }
    for (const landmark of landmarks) {
      projected.fromArray(landmark.position).project(camera);
      const x = (projected.x * 0.5 + 0.5) * width;
      const y = (-projected.y * 0.5 + 0.5) * height;
      landmark.element.hidden = projected.z < -1 || projected.z > 1 || x < 0 || x > width || y < 0 || y > height;
      landmark.element.style.display = landmark.element.hidden ? 'none' : '';
      landmark.element.style.transform = `translate3d(${x.toFixed(1)}px,${y.toFixed(1)}px,0) translate(-50%,-50%)`;
    }
  }

  function updateClearances(force = false) {
    const active = activeClearances(snapshot.clearances, snapshot.clearancesAvailable);
    const signature = `${snapshot.clearancesAvailable}:${active.map(value => `${value.flightId}:${value.runway}:${value.kind}:${value.expiresAt}`).sort().join('|')}`;
    if (!force && clearanceSignature === signature) return;
    clearanceSignature = signature;
    for (const highlight of scenery.runwayHighlights) {
      const current = active.filter(value => highlight.runway.names.includes(String(value.runway).toUpperCase().trim()));
      highlight.mesh.visible = current.length > 0;
      highlight.material.color.set(current.some(value => value.kind === 'Landing') ? 0x73c4ff : 0x6de0bc);
      const landmark = landmarks.find(value => value.key === highlight.runway.names[0]);
      const state = !snapshot.clearancesAvailable ? 'ATC unavailable'
        : current.length ? current.map(value => `${value.kind.toUpperCase()} ${value.callsign || ''}`.trim()).join(' · ')
          : 'No active clearance';
      if (landmark) landmark.element.textContent = `${highlight.runway.names.join(' / ')} · ${state}`;
    }
  }

  function scheduleExpiry() {
    clearTimeout(expiryTimer);
    expiryTimer = 0;
    if (disposed || document.hidden) return;
    const active = activeClearances(snapshot.clearances, snapshot.clearancesAvailable);
    if (!active.length) return;
    const next = Math.min(...active.map(value => Date.parse(value.expiresAt)));
    expiryTimer = setTimeout(() => {
      if (disposed) return;
      updateClearances();
      invalidate();
      scheduleExpiry();
    }, Math.min(2147483647, Math.max(20, next - Date.now() + 10)));
  }

  function applyAtmosphere() {
    const palette = PALETTES[timeOfDay];
    const cloudiness = weatherStyle.cloudiness;
    const overcast = new THREE.Color(timeOfDay === 'night' ? '#162837' : '#71838e');
    scenery.skyMaterial.uniforms.topColor.value.set(palette.sky).lerp(overcast, cloudiness * 0.53);
    scenery.skyMaterial.uniforms.horizonColor.value.set(palette.horizon).lerp(overcast, cloudiness * 0.68);
    scenery.skyMaterial.uniforms.bottomColor.value.set(palette.ground);
    scenery.skyMaterial.uniforms.sunColor.value.set(palette.sun);
    scenery.skyMaterial.uniforms.sunStrength.value = (timeOfDay === 'night' ? 0.04 : 0.55) * (1 - cloudiness);
    scenery.skyMaterial.uniforms.sunDirection.value.fromArray(palette.sunPosition).normalize();
    scenery.cloudMaterial.color.set(timeOfDay === 'night' ? '#54677f' : weatherStyle.storm ? '#627787' : '#e2e7e9');
    scenery.cloudMaterial.opacity = 0.08 + cloudiness * 0.36;
    scene.fog.color.copy(scenery.skyMaterial.uniforms.horizonColor.value);
    scene.fog.density = weatherStyle.fogDensity + (timeOfDay === 'night' ? 0.000035 : 0);
    sun.color.set(palette.sun);
    sun.intensity = palette.sunIntensity * (1 - cloudiness * 0.6);
    sun.position.fromArray(palette.sunPosition);
    hemisphere.intensity = palette.ambient * (1 - cloudiness * 0.22);
    renderer.toneMappingExposure = palette.exposure;
    scenery.materials.asphalt.roughness = 0.87 - weatherStyle.wetness * 0.68;
    scenery.materials.asphalt.metalness = 0.08 + weatherStyle.wetness * 0.3;
    scenery.materials.apron.roughness = 0.82 - weatherStyle.wetness * 0.48;
    scenery.materials.apron.metalness = 0.05 + weatherStyle.wetness * 0.13;
    scenery.materials.ground.color.set(weatherStyle.kind === 'snow' ? 0xadb8b8 : 0x233e42);
    scenery.materials.apron.color.set(weatherStyle.kind === 'snow' ? 0x9ca9b0 : 0x788486);
    scenery.facadeMaterial.emissiveIntensity = timeOfDay === 'day' ? 0.16 : timeOfDay === 'night' ? 1.15 : 0.65;
    scenery.materials.warm.emissiveIntensity = palette.lamps * 0.6;
    scenery.lightMaterial.color.setScalar(palette.lamps);
    scenery.poolMaterial.opacity = timeOfDay === 'day' ? 0.1 : 0.65;
    scenery.lampPools.forEach(light => { light.intensity = 1900 * palette.lamps; });
    scenery.windsock.rotation.y = weatherStyle.windDirection;
    scenery.windsock.rotation.x = 0.1 + Math.max(0, 1 - weatherStyle.wind / 22) * 0.65;
    rain.object.visible = weatherStyle.kind === 'rain';
    snow.object.visible = weatherStyle.kind === 'snow';
    rain.material.opacity = weatherStyle.storm ? 0.35 : 0.23;
    sun.shadow.needsUpdate = true;
    invalidate();
  }

  function updateEnvironment() {
    const environmentScene = new THREE.Scene();
    const environmentSky = scenery.sky.clone();
    environmentSky.position.set(0, 0, 0);
    environmentScene.add(environmentSky);
    const generator = new THREE.PMREMGenerator(renderer);
    try {
      const next = generator.fromScene(environmentScene, 0.045, 1, 18000, { size: 128 });
      environmentTarget?.dispose();
      environmentTarget = next;
      scene.environment = next.texture;
      scene.environmentIntensity = timeOfDay === 'night' ? 0.35 : 0.7;
    } finally {
      generator.dispose();
      environmentScene.clear();
    }
  }

  function createPrecipitation() {
    const rainCount = 2100;
    const snowCount = 1250;
    const rainPositions = new Float32Array(rainCount * 6);
    const snowPositions = new Float32Array(snowCount * 3);
    for (let index = 0; index < rainCount; index++) {
      const offset = index * 6;
      rainPositions[offset] = (random() - 0.5) * 2200;
      rainPositions[offset + 1] = 3 + random() * 490;
      rainPositions[offset + 2] = (random() - 0.5) * 1600;
      rainPositions[offset + 3] = rainPositions[offset] + 1.1;
      rainPositions[offset + 4] = rainPositions[offset + 1] - 6.5;
      rainPositions[offset + 5] = rainPositions[offset + 2];
    }
    for (let index = 0; index < snowCount; index++) {
      snowPositions[index * 3] = (random() - 0.5) * 1800;
      snowPositions[index * 3 + 1] = 3 + random() * 420;
      snowPositions[index * 3 + 2] = (random() - 0.5) * 1500;
    }
    const rainGeometry = resources.geometry(new THREE.BufferGeometry());
    const rainAttribute = new THREE.BufferAttribute(rainPositions, 3).setUsage(THREE.DynamicDrawUsage);
    rainGeometry.setAttribute('position', rainAttribute);
    const rainMaterial = resources.material(new THREE.LineBasicMaterial({
      color: 0xbacfe3, transparent: true, opacity: 0.23, depthWrite: false,
    }));
    const rainObject = new THREE.LineSegments(rainGeometry, rainMaterial);
    rainObject.frustumCulled = false;
    scene.add(rainObject);
    const snowGeometry = resources.geometry(new THREE.BufferGeometry());
    const snowAttribute = new THREE.BufferAttribute(snowPositions, 3).setUsage(THREE.DynamicDrawUsage);
    snowGeometry.setAttribute('position', snowAttribute);
    const snowMaterial = resources.material(new THREE.PointsMaterial({
      color: 0xe1e9f3, size: 1.7, sizeAttenuation: true, transparent: true, opacity: 0.78, depthWrite: false,
    }));
    const snowObject = new THREE.Points(snowGeometry, snowMaterial);
    snowObject.frustumCulled = false;
    scene.add(snowObject);
    rain = { object: rainObject, positions: rainPositions, attribute: rainAttribute, material: rainMaterial, count: rainCount };
    snow = { object: snowObject, positions: snowPositions, attribute: snowAttribute, material: snowMaterial, count: snowCount };
  }

  function animateWeather(delta) {
    const windX = Math.sin(weatherStyle.windDirection) * weatherStyle.wind * 0.24;
    const windZ = Math.cos(weatherStyle.windDirection) * weatherStyle.wind * 0.24;
    if (rain.object.visible) {
      const positions = rain.positions;
      const count = quality === 'high' ? rain.count : Math.floor(rain.count * 0.55);
      for (let index = 0; index < count; index++) {
        const offset = index * 6;
        positions[offset] += windX * delta;
        positions[offset + 1] -= 145 * delta;
        positions[offset + 2] += windZ * delta;
        if (positions[offset + 1] < 1) {
          positions[offset] = (random() - 0.5) * 2200;
          positions[offset + 1] = 430 + random() * 60;
          positions[offset + 2] = (random() - 0.5) * 1600;
        }
        positions[offset + 3] = positions[offset] + windX * 0.045;
        positions[offset + 4] = positions[offset + 1] - 6.5;
        positions[offset + 5] = positions[offset + 2] + windZ * 0.045;
      }
      rain.attribute.needsUpdate = true;
    }
    if (snow.object.visible) {
      const positions = snow.positions;
      const count = quality === 'high' ? snow.count : Math.floor(snow.count * 0.55);
      for (let index = 0; index < count; index++) {
        const offset = index * 3;
        positions[offset] += (windX + Math.sin(elapsed + index) * 1.7) * delta;
        positions[offset + 1] -= (9 + index % 5) * delta;
        positions[offset + 2] += windZ * delta;
        if (positions[offset + 1] < 1) {
          positions[offset] = (random() - 0.5) * 1800;
          positions[offset + 1] = 390 + random() * 30;
          positions[offset + 2] = (random() - 0.5) * 1500;
        }
      }
      snow.attribute.needsUpdate = true;
    }
  }

  function resize() {
    if (disposed || !renderer) return;
    const bounds = hostElement.getBoundingClientRect();
    width = Math.max(1, bounds.width);
    height = Math.max(1, bounds.height);
    const pixelRatio = Math.min(window.devicePixelRatio || 1, quality === 'high' ? 1.65 : 1);
    renderer.setPixelRatio(pixelRatio);
    renderer.setSize(width, height, false);
    composer.setPixelRatio(pixelRatio);
    composer.setSize(width, height);
    camera.aspect = width / height;
    // Shift the optical center away from the desktop's 390px operational panel.
    camera.setViewOffset(width, height, -panelCameraOffset(width), 0, width, height);
    invalidate();
  }

  function updateCamera(delta) {
    if (followingId && planes.has(followingId)) {
      const plane = planes.get(followingId);
      const target = plane.group.position;
      const amount = motion ? 1 - Math.exp(-delta * 2.4) : 1;
      controls.target.lerp(target, amount);
      cameraOffset.copy(target).add(followOffset);
      camera.position.lerp(cameraOffset, amount);
    } else if (cameraTransition) {
      const amount = motion ? 1 - Math.exp(-delta * 3.5) : 1;
      camera.position.lerp(cameraTransition.position, amount);
      controls.target.lerp(cameraTransition.target, amount);
      if (camera.position.distanceToSquared(cameraTransition.position) < 0.03) cameraTransition = null;
    }
    if (motion || followingId || cameraTransition) controls.update();
  }

  function frame(timestamp) {
    raf = 0;
    if (disposed || document.hidden) return;
    try {
      const delta = lastFrame ? Math.min(0.06, Math.max(0, (timestamp - lastFrame) / 1000)) : 1 / 60;
      lastFrame = timestamp;
      updateClearances();
      if (motion) {
        elapsed += delta;
        for (const plane of planes.values()) {
          if (plane.flight.status === 'Cancelled' || !snapshot.flightsAvailable) continue;
          plane.elapsed += delta;
          const desired = flightPose(plane.flight, plane.elapsed);
          plane.pose = interpolatePose(plane.pose, desired, 1 - Math.exp(-delta * 1.45));
          plane.beacon.visible = Math.sin(elapsed * 2.1 + hash(plane.flight.flightId)) > 0.82;
          applyPose(plane);
        }
        for (const car of scenery.dynamicVehicles) {
          car.group.position.x += car.speed * car.direction * delta;
          if (Math.abs(car.group.position.x) > 890) car.group.position.x = -car.direction * 887;
        }
        for (const cloud of scenery.clouds) {
          cloud.position.x += (0.75 + weatherStyle.wind * 0.03) * delta;
          if (cloud.position.x > 3200) cloud.position.x = -3200;
        }
        scenery.windsock.rotation.z = Math.sin(elapsed * 2) * Math.min(weatherStyle.wind / 400, 0.075);
        animateWeather(delta);
      }
      updateCamera(delta);
      scenery.sky.position.copy(camera.position);
      updateLabels();
      if (quality === 'high') composer.render(delta);
      else renderer.render(scene, camera);
      if (motion) invalidate();
    } catch (error) {
      console.error('Airport rendering stopped.', error);
      notify('SceneUnavailable', `Airport rendering stopped: ${error instanceof Error ? error.message : String(error)}. Retry the scene or use the operational panels.`);
      dispose();
    }
  }

  function pointerDown(event) {
    pointers.add(event.pointerId);
    if (pointers.size > 1) {
      if (pointerStart) pointerStart.dragged = true;
      return;
    }
    if (event.button === 0) pointerStart = { id: event.pointerId, x: event.clientX, y: event.clientY, dragged: false };
  }

  function pointerMove(event) {
    if (pointerStart && Math.hypot(event.clientX - pointerStart.x, event.clientY - pointerStart.y) > 6) pointerStart.dragged = true;
  }

  function pointerUp(event) {
    pointers.delete(event.pointerId);
    const start = pointerStart;
    if (start?.id === event.pointerId) pointerStart = null;
    if (!start || start.id !== event.pointerId || start.dragged || event.button !== 0 || event.type !== 'pointerup') return;
    const rect = canvas.getBoundingClientRect();
    pointer.set((event.clientX - rect.left) / rect.width * 2 - 1, -(event.clientY - rect.top) / rect.height * 2 + 1);
    raycaster.setFromCamera(pointer, camera);
    const intersections = raycaster.intersectObjects([...planes.values()].map(plane => plane.group), true);
    const id = intersections[0]?.object.userData.flightId;
    if (id) selectFlight(id);
  }

  function keyDown(event) {
    if (event.ctrlKey || event.metaKey || event.altKey || event.target !== canvas ||
      event.target.closest('input,textarea,select,[contenteditable="true"]')) return;
    if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', '+', '=', '-', '_', 'Home'].includes(event.key)) return;
    event.preventDefault();
    if (event.key === 'Home') { api.setView('overview'); return; }
    followingId = null;
    cameraTransition = null;
    cameraOffset.copy(camera.position).sub(controls.target);
    sphere.setFromVector3(cameraOffset);
    const step = event.shiftKey ? 0.13 : 0.055;
    if (event.key === 'ArrowLeft') sphere.theta -= step;
    if (event.key === 'ArrowRight') sphere.theta += step;
    if (event.key === 'ArrowUp') sphere.phi = Math.max(0.07, sphere.phi - step);
    if (event.key === 'ArrowDown') sphere.phi = Math.min(Math.PI / 2 - 0.035, sphere.phi + step);
    if (event.key === '+' || event.key === '=') sphere.radius = Math.max(controls.minDistance, sphere.radius * 0.89);
    if (event.key === '-' || event.key === '_') sphere.radius = Math.min(controls.maxDistance, sphere.radius * 1.12);
    cameraOffset.setFromSpherical(sphere);
    camera.position.copy(controls.target).add(cameraOffset);
    controls.update();
    invalidate();
  }

  function dispose() {
    if (disposed) return;
    disposed = true;
    ready = false;
    cancelAnimationFrame(raf);
    clearTimeout(expiryTimer);
    raf = 0;
    expiryTimer = 0;
    observer?.disconnect();
    listeners.splice(0).forEach(remove => remove());
    controls?.dispose();
    for (const plane of planes.values()) plane.label.remove();
    planes.clear();
    labelLayer.remove();
    canvas.remove();
    for (const disposable of disposables) disposable.dispose();
    composer?.dispose();
    environmentTarget?.dispose();
    sun?.shadow.dispose();
    resources.dispose();
    // InstancedMesh owns an instance-color texture independently of shared geometry.
    scene.traverse(object => { if (object.isInstancedMesh) object.dispose(); });
    scene.clear();
    renderer?.dispose();
    if (!lost) {
      if (renderer) renderer.forceContextLoss();
      else context?.getExtension('WEBGL_lose_context')?.loseContext();
    }
  }

  const api = {
    update(nextSnapshot) {
      if (disposed) return;
      snapshot = nextSnapshot && typeof nextSnapshot === 'object' ? nextSnapshot : {};
      const flights = normalizeFlights(snapshot.flights);
      for (const [id, plane] of planes) {
        if (!flights.has(id)) {
          scene.remove(plane.group, plane.selection);
          plane.label.remove();
          planes.delete(id);
        }
      }
      for (const [id, flight] of flights) {
        let plane = planes.get(id);
        if (!plane) {
          plane = {
            ...makeAircraft(flight), flight, elapsed: 0,
            pose: flightPose(flight), label: createLabel(flight),
          };
          planes.set(id, plane);
          scene.add(plane.group, plane.selection);
        } else {
          if (flight.status !== plane.flight.status) plane.elapsed = 0;
          // Cancellation freezes the current illustrated location, including in flight.
          if (flight.status !== 'Cancelled' && !motion) plane.pose = flightPose(flight, plane.elapsed);
          plane.flight = flight;
        }
        if (flight.status === 'Cancelled') plane.beacon.visible = false;
        updateLabel(plane);
        applyPose(plane);
      }
      weather = snapshot.weather ?? null;
      weatherStyle = weatherAppearance(weather);
      applyAtmosphere();
      updateClearances(true);
      scheduleExpiry();
      if (!motion) updateCamera(0);
      invalidate();
    },
    setView(name) {
      if (disposed || !Object.hasOwn(VIEWS, name)) return;
      followingId = null;
      const view = VIEWS[name];
      cameraTransition = { position: new THREE.Vector3(...view.position), target: new THREE.Vector3(...view.target) };
      if (!motion) {
        camera.position.copy(cameraTransition.position);
        controls.target.copy(cameraTransition.target);
        cameraTransition = null;
        controls.update();
      }
      invalidate();
    },
    setTimeOfDay(name) {
      if (disposed || !Object.hasOwn(PALETTES, name)) return;
      timeOfDay = name;
      applyAtmosphere();
      updateEnvironment();
    },
    setQuality(name) {
      if (disposed || !['high', 'balanced'].includes(name)) return;
      quality = name;
      bloom.enabled = quality === 'high';
      const shadowSize = quality === 'high' ? 2048 : 1024;
      if (sun.shadow.mapSize.x !== shadowSize) {
        sun.shadow.mapSize.set(shadowSize, shadowSize);
        sun.shadow.map?.dispose();
        sun.shadow.map = null;
      }
      rain.object.geometry.setDrawRange(0, Math.floor(rain.count * (quality === 'high' ? 1 : 0.55)) * 2);
      snow.object.geometry.setDrawRange(0, Math.floor(snow.count * (quality === 'high' ? 1 : 0.55)));
      resize();
    },
    getMotion() {
      return motion;
    },
    setMotion(enabled) {
      if (disposed) return;
      motionChosen = true;
      motion = Boolean(enabled);
      controls.enableDamping = motion;
      if (!motion) {
        cameraTransition = null;
        controls.update();
        for (const plane of planes.values()) plane.beacon.visible = plane.flight.status !== 'Cancelled';
      }
      lastFrame = 0;
      invalidate();
    },
    setLabels(enabled) {
      if (disposed) return;
      labelsEnabled = Boolean(enabled);
      invalidate();
    },
    focusFlight(idOrNull) {
      if (disposed) return;
      selectedId = typeof idOrNull === 'string' && idOrNull ? idOrNull : null;
      followingId = selectedId;
      cameraTransition = null;
      for (const plane of planes.values()) {
        updateLabel(plane);
        plane.selection.visible = plane.flight.flightId === selectedId;
      }
      if (!motion) updateCamera(0);
      invalidate();
    },
    dispose,
  };
  let makeAircraft;

  try {
    canvas.setAttribute('aria-label', 'Interactive 3D airport. Drag to orbit, scroll or pinch to zoom, right-drag to pan. Arrow keys orbit, plus and minus zoom, Home resets. Aircraft positions illustrate workflow states, not GPS.');
    canvas.setAttribute('role', 'application');
    canvas.tabIndex = 0;
    canvas.style.cssText = 'display:block;width:100%;height:100%;position:absolute;inset:0;touch-action:none;';
    labelLayer.style.cssText = 'position:absolute;inset:0;overflow:hidden;pointer-events:none;';
    labelLayer.setAttribute('aria-label', 'Airport aircraft and landmarks');
    context = canvas.getContext('webgl2', { alpha: false, antialias: true, powerPreference: 'high-performance' });
    if (!context) throw new Error('WebGL 2 is unavailable. Enable hardware acceleration or use a WebGL 2 capable browser; the operational panels remain available.');
    renderer = new THREE.WebGLRenderer({ canvas, context, antialias: true, alpha: false, logarithmicDepthBuffer: true });
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    scene.fog = new THREE.FogExp2(0x859ba9, 0.00012);
    hemisphere = new THREE.HemisphereLight(0xc0d6f1, 0x34443b, 1.25);
    scene.add(hemisphere);
    sun = new THREE.DirectionalLight(0xffd5a2, 2.5);
    sun.position.set(-1100, 390, -750);
    sun.castShadow = true;
    sun.shadow.mapSize.set(2048, 2048);
    sun.shadow.camera.left = -1000;
    sun.shadow.camera.right = 1000;
    sun.shadow.camera.top = 900;
    sun.shadow.camera.bottom = -900;
    sun.shadow.camera.near = 100;
    sun.shadow.camera.far = 3300;
    sun.shadow.normalBias = 0.4;
    sun.shadow.bias = -0.0001;
    sun.shadow.radius = 2;
    sun.target.position.set(0, 0, 90);
    scene.add(sun, sun.target);
    scenery = createScenery(scene, resources);
    makeAircraft = createAircraftFactory(resources);
    createPrecipitation();
    camera.position.fromArray(VIEWS.overview.position);
    controls = new OrbitControls(camera, canvas);
    controls.target.fromArray(VIEWS.overview.target);
    controls.enableDamping = motion;
    controls.dampingFactor = 0.075;
    controls.minDistance = 24;
    controls.maxDistance = 3100;
    controls.maxPolarAngle = Math.PI / 2 - 0.03;
    controls.minPolarAngle = 0.06;
    controls.zoomSpeed = 0.85;
    controls.panSpeed = 0.7;
    controls.update();
    composer = new EffectComposer(renderer);
    const renderPass = new RenderPass(scene, camera);
    disposables.push(renderPass);
    const outputPass = new OutputPass();
    disposables.push(outputPass);
    bloom = new UnrealBloomPass(new THREE.Vector2(1, 1), 0.18, 0.5, 1.55);
    disposables.push(bloom);
    composer.addPass(renderPass);
    composer.addPass(bloom);
    composer.addPass(outputPass);
    for (const landmark of scenery.landmarkPositions) {
      const element = document.createElement('span');
      element.className = 'scene-landmark';
      element.textContent = landmark.text;
      element.style.cssText = 'position:absolute;left:0;top:0;pointer-events:none;white-space:nowrap;';
      labelLayer.append(element);
      landmarks.push({ ...landmark, element });
    }
    hostElement.append(canvas, labelLayer);
    listen(canvas, 'pointerdown', pointerDown);
    listen(canvas, 'pointermove', pointerMove);
    listen(canvas, 'pointerup', pointerUp);
    listen(canvas, 'pointercancel', pointerUp);
    listen(canvas, 'keydown', keyDown);
    listen(canvas, 'contextmenu', event => event.preventDefault());
    listen(controls, 'change', invalidate);
    listen(controls, 'start', () => { cameraTransition = null; followingId = null; });
    listen(canvas, 'webglcontextlost', event => {
      event.preventDefault();
      if (disposed) return;
      lost = true;
      notify('SceneUnavailable', 'The graphics context was lost. Retry the airport scene; flight, weather, and tower controls remain available.');
      dispose();
    });
    listen(document, 'visibilitychange', () => {
      cancelAnimationFrame(raf);
      raf = 0;
      lastFrame = 0;
      clearTimeout(expiryTimer);
      if (!document.hidden) {
        updateClearances(true);
        scheduleExpiry();
        invalidate();
      }
    });
    listen(window, 'resize', resize);
    listen(reducedMotion, 'change', () => {
      if (motionChosen) return;
      motion = !reducedMotion.matches;
      controls.enableDamping = motion;
      if (!motion) cameraTransition = null;
      lastFrame = 0;
      invalidate();
    });
    observer = new ResizeObserver(resize);
    observer.observe(hostElement);
    resize();
    applyAtmosphere();
    updateEnvironment();
    ready = true;
    invalidate();
    return api;
  } catch (error) {
    dispose();
    throw new Error(`Unable to initialize the 3D airport: ${error instanceof Error ? error.message : String(error)}`, { cause: error });
  }
}
