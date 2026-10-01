import test from 'node:test';
import assert from 'node:assert/strict';
import * as THREE from 'three';
import { createAircraftFactory } from './aircraft.js';
import { Resources, createScenery } from './scenery.js';

test('procedural aircraft have finite merged geometry, shared GPU assets, and distinct selectable IDs', () => {
  const resources = new Resources();
  const factory = createAircraftFactory(resources);
  const first = factory({ flightId: 'one', aircraftType: 'Boeing 737' });
  const second = factory({ flightId: 'two', aircraftType: 'Airbus A350' });
  assert.notEqual(first.group, second.group);
  assert.ok(first.size < second.size);
  assert.ok(first.gear);
  assert.ok(first.beacon);
  let meshes = 0;
  first.group.traverse(object => {
    assert.equal(object.userData.flightId, 'one');
    if (object.isMesh) {
      meshes++;
      assert.ok(resources.geometries.has(object.geometry));
      object.geometry.computeBoundingBox();
      assert.ok(Number.isFinite(object.geometry.boundingBox.min.x));
      assert.ok(Number.isFinite(object.geometry.boundingBox.max.z));
    }
  });
  assert.ok(meshes < 25, `Airframe batching regressed to ${meshes} meshes.`);
  assert.equal(first.selection.geometry, second.selection.geometry);
  assert.equal(first.selection.visible, false);
  resources.dispose();
});

test('the fuselage nose, cockpit, and engine intakes face +Z with the tail aft', () => {
  const resources = new Resources();
  try {
    const aircraft = createAircraftFactory(resources)({ flightId: 'orientation', aircraftType: 'A320' });
    const fuselage = [...resources.geometries].find(geometry => geometry.type === 'LatheGeometry');
    const profileVertices = fuselage.parameters.points.length;
    assert.equal(fuselage.attributes.position.getZ(0), -17.5);
    assert.equal(fuselage.attributes.position.getZ(profileVertices - 1), 16.5);
    const tail = [...resources.geometries].find(geometry => {
      if (geometry.type !== 'ExtrudeGeometry') return false;
      geometry.computeBoundingBox();
      return geometry.boundingBox.max.y > 7;
    });
    assert.ok(tail.boundingBox.max.z < -10);
    const cockpit = aircraft.group.children.find(object => object.isMesh && object.material.color.getHex() === 0x112d46);
    cockpit.geometry.computeBoundingBox();
    assert.ok(cockpit.geometry.boundingBox.max.z > 14);
    const intakes = aircraft.group.children.find(object => object.isMesh && object.material.color.getHex() === 0x111d2a);
    intakes.geometry.computeBoundingBox();
    assert.ok(intakes.geometry.boundingBox.min.z > 3);
  } finally {
    resources.dispose();
  }
});

test('airport geometry is local, instanced, and starts with no implied runway clearances', () => {
  // A minimal canvas drawing surface exercises geometry construction without WebGL.
  const previousDocument = globalThis.document;
  const context = {
    createImageData: (width, height) => ({ data: new Uint8ClampedArray(width * height * 4) }),
    putImageData() {}, fillRect() {}, fillText() {},
    createRadialGradient: () => ({ addColorStop() {} }),
  };
  globalThis.document = { createElement: () => ({ width: 0, height: 0, getContext: () => context }) };
  const resources = new Resources();
  const scene = new THREE.Scene();
  try {
    const airport = createScenery(scene, resources);
    assert.equal(airport.runwayHighlights.length, 2);
    assert.ok(airport.runwayHighlights.every(highlight => !highlight.mesh.visible));
    assert.ok(airport.landmarkPositions.filter(label => label.key.startsWith('09')).every(label => label.text.includes('unavailable')));
    let instanced = 0;
    let meshes = 0;
    scene.traverse(object => {
      if (object.isMesh) meshes++;
      if (object.isInstancedMesh) instanced++;
      assert.ok([object.position.x, object.position.y, object.position.z].every(Number.isFinite));
    });
    assert.ok(instanced > 15);
    assert.ok(meshes < 180, `Airport batching regressed to ${meshes} meshes.`);
    assert.equal(airport.clouds.length, 32);
    assert.equal(airport.lampPools.length, 4);
    assert.ok(resources.textures.size < 25);
    const ground = scene.children.find(object => object.material === airport.materials.ground);
    assert.equal(ground.castShadow, false);
    assert.equal(ground.receiveShadow, true);
    const matrix = new THREE.Matrix4();
    const position = new THREE.Vector3();
    const scale = new THREE.Vector3();
    const rotation = new THREE.Quaternion();
    let pavementReceivers = 0;
    let buildingCasters = 0;
    scene.traverse(object => {
      if (!object.isMesh || object.geometry.type !== 'BoxGeometry') return;
      for (let index = 0; index < (object.isInstancedMesh ? object.count : 1); index++) {
        if (object.isInstancedMesh) object.getMatrixAt(index, matrix);
        else { object.updateMatrix(); matrix.copy(object.matrix); }
        matrix.decompose(position, rotation, scale);
        if (scale.y <= 0.2 && position.y <= 0.2) {
          assert.equal(object.castShadow, false, 'Instancing must not restore pavement self-shadowing.');
          assert.equal(object.receiveShadow, true);
          pavementReceivers++;
        }
        if (scale.y >= 10 && object.castShadow && object.receiveShadow) buildingCasters++;
      }
    });
    assert.ok(pavementReceivers > 15);
    assert.ok(buildingCasters > 20, 'Real structures must retain cast and receive shadows.');
  } finally {
    scene.traverse(object => { if (object.isInstancedMesh) object.dispose(); });
    resources.dispose();
    if (previousDocument === undefined) delete globalThis.document;
    else globalThis.document = previousDocument;
  }
});

test('owned resources are disposed exactly once, including repeated cleanup requests', () => {
  const resources = new Resources();
  const geometry = resources.geometry(new THREE.BoxGeometry());
  const material = resources.material(new THREE.MeshBasicMaterial());
  const texture = resources.texture(new THREE.Texture());
  const calls = { geometry: 0, material: 0, texture: 0 };
  geometry.addEventListener('dispose', () => { calls.geometry++; });
  material.addEventListener('dispose', () => { calls.material++; });
  texture.addEventListener('dispose', () => { calls.texture++; });
  resources.geometry(geometry);
  resources.dispose();
  resources.dispose();
  assert.deepEqual(calls, { geometry: 1, material: 1, texture: 1 });
});
