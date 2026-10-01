import * as THREE from 'three';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';

export function createAircraftFactory(resources) {
  const standard = options => resources.material(new THREE.MeshStandardMaterial({ dithering: true, ...options }));
  const white = standard({ color: 0xedf0ec, metalness: 0.22, roughness: 0.3 });
  const blue = standard({ color: 0x175da0, metalness: 0.3, roughness: 0.32 });
  const cyan = standard({ color: 0x64d6e5, metalness: 0.25, roughness: 0.35 });
  const silver = standard({ color: 0x74818b, metalness: 0.82, roughness: 0.28 });
  const dark = standard({ color: 0x111d2a, roughness: 0.72 });
  const glass = standard({ color: 0x112d46, metalness: 0.8, roughness: 0.12 });
  const redLight = resources.material(new THREE.MeshBasicMaterial({ color: new THREE.Color(2.8, 0.12, 0.07) }));
  const greenLight = resources.material(new THREE.MeshBasicMaterial({ color: new THREE.Color(0.15, 2.6, 1.1) }));
  const whiteLight = resources.material(new THREE.MeshBasicMaterial({ color: new THREE.Color(2.6, 2.4, 2.1) }));
  const box = resources.geometry(new THREE.BoxGeometry(1, 1, 1));
  const sphere = resources.geometry(new THREE.SphereGeometry(1, 12, 8));
  const engine = resources.geometry(new THREE.CylinderGeometry(1, 0.91, 3.2, 20).rotateX(Math.PI / 2));
  const fan = resources.geometry(new THREE.CircleGeometry(0.78, 16));
  const wheel = resources.geometry(new THREE.CylinderGeometry(0.48, 0.48, 0.32, 10).rotateZ(Math.PI / 2));
  // Positive X rotation maps the lathe's +Y nose to +Z, matching cockpit and intakes.
  const fuselage = resources.geometry(new THREE.LatheGeometry([
    new THREE.Vector2(0.05, -17.5), new THREE.Vector2(0.65, -14.5),
    new THREE.Vector2(1.3, -10), new THREE.Vector2(1.65, -4),
    new THREE.Vector2(1.65, 8), new THREE.Vector2(1.48, 11),
    new THREE.Vector2(1.1, 13.6), new THREE.Vector2(0.45, 15.7),
    new THREE.Vector2(0.04, 16.5),
  ], 24).rotateX(Math.PI / 2));

  function wingGeometry(points, thickness = 0.25) {
    const shape = new THREE.Shape();
    shape.moveTo(points[0][0], points[0][1]);
    points.slice(1).forEach(point => shape.lineTo(point[0], point[1]));
    shape.closePath();
    const geometry = new THREE.ExtrudeGeometry(shape, { depth: thickness, bevelEnabled: false });
    geometry.rotateX(Math.PI / 2);
    return resources.geometry(geometry);
  }
  const wing = wingGeometry([[0, 4], [5, 1], [17, -6], [17, -8], [4, -5], [0, -6]]);
  const stabilizer = wingGeometry([[0, -10.5], [7, -15], [7, -16], [0, -14.5]], 0.18);
  const finShape = new THREE.Shape();
  finShape.moveTo(-11, 0);
  finShape.lineTo(-15.5, 7.6);
  finShape.lineTo(-17.9, 7.6);
  finShape.lineTo(-17.1, 0);
  finShape.closePath();
  const tail = resources.geometry(new THREE.ExtrudeGeometry(finShape, { depth: 0.22, bevelEnabled: false }).rotateY(-Math.PI / 2));
  const prototype = new THREE.Group();
  const mesh = (geometry, material, position, scale, parent = prototype) => {
    const object = new THREE.Mesh(geometry, material);
    object.position.set(...position);
    if (scale) object.scale.set(...scale);
    object.castShadow = true;
    object.receiveShadow = true;
    parent.add(object);
    return object;
  };
  mesh(fuselage, white, [0, 0, 0]);
  mesh(sphere, glass, [0, 0.9, 12.3], [1.25, 0.65, 2.4]);
  mesh(sphere, white, [0, 1.2, 10.8], [1.3, 0.42, 1.65]);
  mesh(tail, blue, [0.1, 0.6, 0]);
  const finAccent = mesh(box, cyan, [0.02, 4.5, -15.4], [0.28, 0.75, 3.4]);
  finAccent.rotation.x = -0.25;
  const winglets = [];
  for (const side of [-1, 1]) {
    mesh(wing, white, [0, -0.35, 0], [side, 1, 1]);
    mesh(stabilizer, white, [0, 0.9, 0], [side, 1, 1]);
    const winglet = mesh(box, blue, [side * 16.85, 0.85, -6.9], [0.2, 2.5, 1.8]);
    winglet.rotation.z = side * -0.2;
    winglets.push(winglet);
    mesh(box, silver, [side * 5.7, -0.8, 1.1], [0.28, 1.7, 2.2]);
    mesh(engine, white, [side * 5.7, -1.55, 1.9]);
    mesh(fan, dark, [side * 5.7, -1.55, 3.53]);
    mesh(sphere, silver, [side * 5.7, -1.55, 3.56], [0.17, 0.17, 0.17]);
    for (let blade = 0; blade < 7; blade++) {
      const angle = blade * Math.PI * 2 / 7;
      const detail = mesh(box, silver, [side * 5.7 + Math.sin(angle) * 0.4, -1.55 + Math.cos(angle) * 0.4, 3.54], [0.065, 0.52, 0.04]);
      detail.rotation.z = -angle + 0.3;
    }
    mesh(box, blue, [side * 1.57, 0.25, -0.5], [0.08, 0.28, 22.5]);
    for (let index = 0; index < 19; index++) mesh(sphere, glass, [side * 1.64, 0.75, 9 - index * 1.03], [0.055, 0.19, 0.15]);
    mesh(box, silver, [side * 1.67, -0.1, 9.8], [0.035, 1.7, 0.76]);
    mesh(box, white, [side * 1.7, -0.1, 9.8], [0.04, 1.53, 0.62]);
    const light = mesh(sphere, side < 0 ? redLight : greenLight, [side * 17.05, 0.04, -6.7], [0.26, 0.19, 0.27]);
    light.castShadow = false;
  }
  const gear = new THREE.Group();
  gear.name = 'landing-gear';
  prototype.add(gear);
  for (const [x, z] of [[-2.2, -2.7], [2.2, -2.7], [0, 10]]) {
    mesh(box, silver, [x, -2.1, z], [0.18, 2, 0.18], gear);
    for (const side of [-1, 1]) mesh(wheel, dark, [x + side * 0.3, -2.87, z], null, gear);
  }
  const beacon = mesh(sphere, redLight, [0, 1.78, 0], [0.17, 0.16, 0.17]);
  beacon.name = 'beacon';
  beacon.castShadow = false;
  const tailLight = mesh(sphere, whiteLight, [0, 0.3, -17.2], [0.16, 0.16, 0.16]);
  tailLight.castShadow = false;

  const selectionMaterial = resources.material(new THREE.MeshBasicMaterial({
    color: 0x7ee4ea, transparent: true, opacity: 0.65, depthWrite: false,
  }));
  const selectionGeometry = resources.geometry(new THREE.RingGeometry(19, 19.45, 64).rotateX(-Math.PI / 2));

  // Bake the many small airframe details into one draw call per shared material.
  prototype.updateMatrixWorld(true);
  const materialGroups = new Map();
  for (const object of [...prototype.children]) {
    if (!object.isMesh || object === beacon) continue;
    const geometry = object.geometry.index ? object.geometry.toNonIndexed() : object.geometry.clone();
    geometry.applyMatrix4(object.matrixWorld);
    if (object.matrixWorld.determinant() < 0) {
      for (const attribute of Object.values(geometry.attributes)) {
        for (let vertex = 0; vertex < attribute.count; vertex += 3) {
          for (let component = 0; component < attribute.itemSize; component++) {
            const first = (vertex + 1) * attribute.itemSize + component;
            const second = (vertex + 2) * attribute.itemSize + component;
            [attribute.array[first], attribute.array[second]] = [attribute.array[second], attribute.array[first]];
          }
        }
      }
    }
    if (!materialGroups.has(object.material)) materialGroups.set(object.material, []);
    materialGroups.get(object.material).push(geometry);
    prototype.remove(object);
  }
  for (const [material, geometries] of materialGroups) {
    const merged = mergeGeometries(geometries);
    if (!merged) throw new Error('The airport could not assemble its procedural aircraft.');
    mesh(resources.geometry(merged), material, [0, 0, 0]);
    geometries.forEach(geometry => geometry.dispose());
  }

  return flight => {
    const group = prototype.clone(true);
    const widebody = /330|350|380|777|787|747/i.test(String(flight.aircraftType ?? ''));
    const regional = /ATR|CRJ|EMB|E1[789]|E90/i.test(String(flight.aircraftType ?? ''));
    const size = widebody ? 1.05 : regional ? 0.61 : 0.72;
    group.scale.setScalar(size);
    group.userData.flightId = flight.flightId;
    group.traverse(object => { object.userData.flightId = flight.flightId; });
    const selection = new THREE.Mesh(selectionGeometry, selectionMaterial);
    selection.visible = false;
    selection.rotation.x = 0;
    return {
      group, selection, size,
      gear: group.getObjectByName('landing-gear'),
      beacon: group.getObjectByName('beacon'),
    };
  };
}
