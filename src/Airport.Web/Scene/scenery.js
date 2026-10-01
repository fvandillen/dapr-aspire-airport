import * as THREE from 'three';
import { CONCOURSES, RUNWAYS, gatePlacement, seededRandom } from './scene-model.js';

export class Resources {
  geometries = new Set();
  materials = new Set();
  textures = new Set();

  geometry(value) { this.geometries.add(value); return value; }
  material(value) { this.materials.add(value); return value; }
  texture(value) { this.textures.add(value); return value; }

  dispose() {
    for (const texture of this.textures) texture.dispose();
    for (const material of this.materials) material.dispose();
    for (const geometry of this.geometries) geometry.dispose();
    this.textures.clear();
    this.materials.clear();
    this.geometries.clear();
  }
}

function canvasTexture(resources, width, height, draw) {
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext('2d');
  if (!context) throw new Error('The airport could not create its local procedural textures.');
  draw(context, width, height);
  const texture = resources.texture(new THREE.CanvasTexture(canvas));
  texture.colorSpace = THREE.SRGBColorSpace;
  return texture;
}

export function createScenery(scene, resources) {
  const random = seededRandom('airport-landscape');
  const boxGeometry = resources.geometry(new THREE.BoxGeometry(1, 1, 1));
  const sphereGeometry = resources.geometry(new THREE.SphereGeometry(1, 10, 6));
  const cylinderGeometry = resources.geometry(new THREE.CylinderGeometry(1, 1, 1, 12));
  const planeGeometry = resources.geometry(new THREE.PlaneGeometry(1, 1));
  const lightInstances = [];
  const paintInstances = [];
  const yellowInstances = [];
  const frameInstances = [];
  const windowInstances = [];
  const lampPools = [];
  const dynamicVehicles = [];
  const landmarkPositions = [];

  const standard = options => resources.material(new THREE.MeshStandardMaterial({ dithering: true, ...options }));
  const materials = {
    ground: standard({ color: 0x233e42, roughness: 1 }),
    asphalt: standard({ color: 0x252e34, roughness: 0.87, metalness: 0.08 }),
    apron: standard({ color: 0x788486, roughness: 0.82, metalness: 0.05 }),
    white: standard({ color: 0xd6dedb, roughness: 0.75 }),
    yellow: standard({ color: 0xe0b74f, roughness: 0.7, emissive: 0x5c430d, emissiveIntensity: 0.14 }),
    roof: standard({ color: 0xb5c1c7, metalness: 0.68, roughness: 0.42 }),
    steel: standard({ color: 0x354653, metalness: 0.8, roughness: 0.34 }),
    glass: standard({ color: 0x314d62, metalness: 0.72, roughness: 0.16, emissive: 0x315472, emissiveIntensity: 0.3 }),
    warm: standard({ color: 0xe8cfa5, roughness: 0.6, emissive: 0xffb966, emissiveIntensity: 0.6 }),
    water: standard({ color: 0x224d65, metalness: 0.72, roughness: 0.28 }),
  };

  const noise = canvasTexture(resources, 256, 256, context => {
    const data = context.createImageData(256, 256);
    for (let index = 0; index < data.data.length; index += 4) {
      const shade = 170 + Math.floor(random() * 65);
      data.data[index] = shade;
      data.data[index + 1] = shade;
      data.data[index + 2] = shade;
      data.data[index + 3] = 255;
    }
    context.putImageData(data, 0, 0);
  });
  noise.wrapS = noise.wrapT = THREE.RepeatWrapping;
  noise.repeat.set(64, 64);
  materials.ground.map = noise;
  materials.asphalt.map = noise;
  materials.apron.map = noise;

  function mesh(geometry, material, position, scale, parent = scene) {
    const object = new THREE.Mesh(geometry, material);
    object.position.set(...position);
    if (scale) object.scale.set(...scale);
    // Flat pavement receives aircraft/building shadows but must not shadow itself.
    const pavement = geometry === boxGeometry && scale?.[1] <= 0.2 && position[1] <= 0.2;
    object.castShadow = geometry !== planeGeometry && !pavement;
    object.receiveShadow = true;
    parent.add(object);
    return object;
  }
  const box = (material, position, scale, parent) => mesh(boxGeometry, material, position, scale, parent);
  const cylinder = (material, position, scale, parent) => mesh(cylinderGeometry, material, position, scale, parent);
  const instance = (list, position, scale, rotation = 0, color) => list.push({ position, scale, rotation, color });

  function batch(geometry, material, list, shadows = false) {
    if (!list.length) return null;
    const result = new THREE.InstancedMesh(geometry, material, list.length);
    const transform = new THREE.Object3D();
    list.forEach((item, index) => {
      transform.position.set(...item.position);
      transform.scale.set(...item.scale);
      transform.rotation.set(0, item.rotation ?? 0, 0);
      transform.updateMatrix();
      result.setMatrixAt(index, transform.matrix);
      if (item.color) result.setColorAt(index, item.color);
    });
    result.castShadow = shadows;
    result.receiveShadow = true;
    result.instanceMatrix.needsUpdate = true;
    scene.add(result);
    return result;
  }

  const paint = (x, z, width, depth, yellow = false, rotation = 0, y = 0.32) =>
    instance(yellow ? yellowInstances : paintInstances, [x, y, z], [width, 0.035, depth], rotation);

  const lamp = (x, z, color = 0x83b8ff, y = 0.8, size = 0.68) =>
    instance(lightInstances, [x, y, z], [size, size * 0.65, size], 0, new THREE.Color(color).multiplyScalar(2.5));

  function line(points, width, yellow = false) {
    for (let index = 1; index < points.length; index++) {
      const [x1, z1] = points[index - 1];
      const [x2, z2] = points[index];
      paint((x1 + x2) / 2, (z1 + z2) / 2, width, Math.hypot(x2 - x1, z2 - z1), yellow, Math.atan2(x2 - x1, z2 - z1));
    }
  }

  function sign(text, width, depth, position, rotation = 0, background = null) {
    const texture = canvasTexture(resources, 512, 128, (context, w, h) => {
      if (background) {
        context.fillStyle = background;
        context.fillRect(0, 0, w, h);
      }
      context.fillStyle = '#f0f3e9';
      context.textAlign = 'center';
      context.textBaseline = 'middle';
      context.font = 'bold 88px sans-serif';
      context.fillText(text, w / 2, h / 2 + 2, w - 28);
    });
    const material = standard({ map: texture, transparent: !background, roughness: 0.82, depthWrite: Boolean(background), polygonOffset: true, polygonOffsetFactor: -1 });
    const object = mesh(planeGeometry, material, position, [width, depth, 1]);
    object.rotation.set(-Math.PI / 2, 0, rotation);
    object.castShadow = false;
    return object;
  }

  mesh(planeGeometry, materials.ground, [0, -0.15, 0], [40000, 40000, 1]).rotation.x = -Math.PI / 2;
  box(materials.apron, [0, -0.015, 252], [830, 0.15, 447]);
  box(materials.apron, [505, -0.02, 240], [200, 0.13, 350]);
  box(materials.apron, [-495, -0.02, 240], [150, 0.13, 350]);
  box(materials.asphalt, [0, 0.01, -27], [1580, 0.1, 26]);
  box(materials.asphalt, [0, 0, -191], [1580, 0.09, 22]);
  line([[-780, -27], [780, -27]], 0.42, true);
  line([[-780, -191], [780, -191]], 0.35, true);
  for (const x of [-737, -410, 0, 410, 737]) {
    box(materials.asphalt, [x, -0.01, -138], [25, 0.1, 250]);
    line([[x, -275], [x, 25]], 0.38, true);
    for (let z = -280; z < 30; z += 22) {
      lamp(x - 13.5, z);
      lamp(x + 13.5, z);
    }
  }
  for (let x = -770; x <= 770; x += 23) {
    for (const z of [-41, -13, -204, -178]) lamp(x, z);
  }

  const runwayHighlights = RUNWAYS.map(runway => {
    const z = runway.z;
    box(materials.asphalt, [0, 0.035, z], [runway.length, 0.14, runway.width]);
    paint(0, z - 19.6, 1560, 0.6);
    paint(0, z + 19.6, 1560, 0.6);
    for (let x = -590; x <= 590; x += 58) paint(x, z, 28, 0.75);
    for (const side of [-1, 1]) {
      const threshold = side * 750;
      for (let stripe = -4; stripe <= 4; stripe++) {
        if (stripe !== 0) paint(threshold, z + stripe * 3.7, 45, 1.85);
      }
      for (const mark of [465, 550]) {
        paint(side * mark, z - 12, 36, 3.5);
        paint(side * mark, z + 12, 36, 3.5);
      }
      // Text runs lengthwise down each runway, with reciprocal designations facing arrivals.
      sign(side < 0 ? runway.names[0] : runway.names[1], 29, 34, [side * 673, 0.34, z], side < 0 ? -Math.PI / 2 : Math.PI / 2);
      for (let offset = -18; offset <= 18; offset += 3) lamp(side * 782, z + offset, 0x78ffbe, 0.4);
      for (let step = 0; step < 10; step++) {
        const x = side * (805 + step * 17);
        lamp(x, z, 0xffd8a1, 0.7, 0.85);
        if (step === 5 || step === 8) {
          for (let offset = -12; offset <= 12; offset += 4) lamp(x, z + offset, 0xffd8a1, 0.7, 0.85);
        }
      }
      for (let papi = 0; papi < 4; papi++) lamp(side * 475, z + 30 + papi * 3, papi < 2 ? 0xff483d : 0xffece0, 0.8);
    }
    for (let x = -770; x <= 770; x += 24) {
      lamp(x, z - 22, Math.abs(x) > 540 ? 0xffc977 : 0xe3edff);
      lamp(x, z + 22, Math.abs(x) > 540 ? 0xffc977 : 0xe3edff);
      if (Math.abs(x) < 635) lamp(x, z, 0xf5ebd6, 0.3, 0.36);
    }
    const material = resources.material(new THREE.MeshBasicMaterial({
      color: 0x56dacb, transparent: true, opacity: 0.13, depthWrite: false,
    }));
    const highlight = mesh(planeGeometry, material, [0, 0.37, z], [1500, 36, 1]);
    highlight.rotation.x = -Math.PI / 2;
    highlight.visible = false;
    highlight.castShadow = false;
    landmarkPositions.push({ key: runway.names[0], text: `${runway.names.join(' / ')} · ATC unavailable`, position: [-480, 7, z - 31] });
    return { runway, mesh: highlight, material };
  });

  // Apron concrete joints, equipment lanes, and individually addressable B–G stands.
  const joints = [];
  for (let x = -400; x <= 400; x += 25) instance(joints, [x, 0.08, 250], [0.12, 0.015, 430]);
  for (let z = 42; z <= 462; z += 25) instance(joints, [0, 0.08, z], [800, 0.015, 0.12]);
  batch(boxGeometry, standard({ color: 0x59696e, roughness: 1, polygonOffset: true, polygonOffsetFactor: -1, polygonOffsetUnits: -2 }), joints);
  line([[-375, 61], [375, 61]], 0.5, true);
  line([[-375, 84], [375, 84]], 0.23);
  for (const x of [-370, 370]) {
    line([[x, 64], [x, 435]], 0.5, true);
    for (let z = 80; z < 435; z += 22) lamp(x + (x < 0 ? -11 : 11), z, 0x86b9ff);
  }

  const facade = canvasTexture(resources, 512, 128, (context, w, h) => {
    context.fillStyle = '#153142';
    context.fillRect(0, 0, w, h);
    for (let row = 0; row < 4; row++) {
      for (let column = 0; column < 24; column++) {
        context.fillStyle = random() > 0.3 ? `rgba(255,211,150,${0.22 + random() * 0.55})` : '#315b73';
        context.fillRect(column * 22 + 2, row * 32 + 3, 18, 25);
        context.fillStyle = 'rgba(196,218,219,0.35)';
        context.fillRect(column * 22, row * 32, 1, 32);
      }
    }
  });
  const facadeMaterial = standard({
    map: facade, emissiveMap: facade, color: 0xa4c5da, metalness: 0.6, roughness: 0.2,
    emissive: 0xffd29e, emissiveIntensity: 0.65,
  });
  const facadeMaterials = [facadeMaterial, facadeMaterial, materials.roof, materials.steel, facadeMaterial, facadeMaterial];
  box(facadeMaterials, [0, 15, 458], [690, 30, 66]);
  box(materials.roof, [0, 31, 458], [706, 2, 78]);
  box(materials.steel, [0, 3, 459], [710, 6, 73]);
  for (let x = -335; x <= 335; x += 13.5) {
    instance(frameInstances, [x, 17, 423.9], [0.6, 29, 0.9]);
    instance(frameInstances, [x, 17, 492], [0.6, 29, 0.9]);
    instance(frameInstances, [x, 32.6, 458], [0.7, 1.2, 78]);
  }
  for (const y of [10, 20]) {
    box(materials.roof, [0, y, 424], [690, 0.45, 0.5]);
    box(materials.roof, [0, y, 492], [690, 0.45, 0.5]);
  }
  box(materials.glass, [0, 27, 461], [78, 51, 73]);
  box(materials.roof, [0, 53, 461], [84, 1.5, 78]);
  for (let x = -35; x <= 35; x += 7) {
    instance(frameInstances, [x, 29, 423.9], [0.65, 48, 0.8]);
    instance(frameInstances, [x, 29, 498], [0.65, 48, 0.8]);
  }
  sign('AIRPORT', 130, 21, [0, 54, 461], 0, '#213b4b');
  landmarkPositions.push({ key: 'terminal', text: 'TERMINAL · B—G', position: [0, 66, 457] });

  for (let index = 0; index < CONCOURSES.length; index++) {
    const letter = CONCOURSES[index];
    const x = -275 + index * 110;
    box(facadeMaterials, [x, 7.4, 270], [20, 14.8, 316]);
    box(materials.roof, [x, 15.5, 270], [25, 1.4, 325]);
    box(materials.glass, [x, 17, 116], [36, 22, 38]);
    box(materials.roof, [x, 28.4, 116], [41, 1.5, 43]);
    sign(letter, 17, 20, [x, 29.4, 116]);
    for (let z = 127; z < 421; z += 10.5) {
      instance(frameInstances, [x - 10.3, 8, z], [0.6, 14, 0.65]);
      instance(frameInstances, [x + 10.3, 8, z], [0.6, 14, 0.65]);
    }
    for (let gate = 1; gate <= 29; gate++) {
      const stand = gatePlacement(`${letter}${gate}`);
      const side = stand.x < x ? -1 : 1;
      line([[x + side * 52, stand.z], [x + side * 19, stand.z]], 0.3, true);
      paint(x + side * 34, stand.z - 4, 0.28, 8, true);
      const bridge = box(facadeMaterial, [x + side * 18, 6.5, stand.z + 5], [17, 3, 3]);
      bridge.rotation.y = side * 0.17;
      box(materials.steel, [x + side * 25.8, 3, stand.z + 3.5], [1.3, 6, 1.3]);
      box(materials.roof, [x + side * 26, 6.6, stand.z + 3.6], [3.7, 3.4, 5]);
      if (gate % 6 === 1) {
        box(materials.white, [x + side * 40, 1.4, stand.z + 8], [5, 2.5, 2.5]);
        box(materials.steel, [x + side * 42, 1.45, stand.z + 8], [1.2, 1, 2.55]);
        for (let cart = 0; cart < 3; cart++) box(materials.roof, [x + side * 47, 0.7, stand.z + 3 + cart * 3.5], [2, 1.1, 2.6]);
      }
    }
    for (let z = 154; z < 415; z += 72) {
      box(materials.steel, [x, 17, z], [10, 2, 7]);
      box(materials.roof, [x, 18.5, z], [11, 1, 8]);
    }
  }

  // Four tall floodlight masts illuminate the apron without per-lamp shadow maps.
  for (const [x, z] of [[-377, 132], [-377, 376], [377, 132], [377, 376]]) {
    cylinder(materials.steel, [x, 24, z], [0.8, 48, 0.8]);
    box(materials.steel, [x, 48, z], [12, 0.6, 2]);
    for (const offset of [-4.5, -1.5, 1.5, 4.5]) {
      lamp(x + offset, z, 0xffdda7, 47.8, 1.1);
      box(materials.roof, [x + offset, 48.1, z], [2.4, 0.8, 2.2]);
    }
    const light = new THREE.PointLight(0xffd7a1, 1900, 250, 2);
    light.position.set(x, 44, z);
    scene.add(light);
    lampPools.push(light);
  }

  // Observation tower: tapered shaft, octagonal glazed cab, roof, antennas, and beacon.
  const towerX = -431;
  const towerZ = 433;
  const shaftGeometry = resources.geometry(new THREE.CylinderGeometry(6, 10, 70, 8));
  mesh(shaftGeometry, materials.white, [towerX, 36, towerZ]);
  cylinder(materials.steel, [towerX, 70.5, towerZ], [19, 4, 19]);
  const towerCabGeometry = resources.geometry(new THREE.CylinderGeometry(20, 16, 12, 8));
  mesh(towerCabGeometry, materials.glass, [towerX, 78, towerZ]);
  cylinder(materials.warm, [towerX, 73.6, towerZ], [17, 0.7, 17]);
  cylinder(materials.roof, [towerX, 85, towerZ], [23, 2, 23]);
  cylinder(materials.steel, [towerX, 91, towerZ], [0.45, 12, 0.45]);
  lamp(towerX, towerZ, 0xff4e42, 97.2, 0.9);
  for (let index = 0; index < 8; index++) {
    const angle = index * Math.PI / 4;
    box(materials.roof, [towerX + Math.sin(angle) * 18, 78, towerZ + Math.cos(angle) * 18], [0.7, 13, 0.7]);
  }
  box(materials.white, [towerX, 5, towerZ + 19], [37, 10, 22]);
  landmarkPositions.push({ key: 'tower', text: 'AIR TRAFFIC CONTROL', position: [towerX, 108, towerZ] });

  for (let index = 0; index < 3; index++) {
    const x = 620 + index * 93;
    box(materials.apron, [x, 0.01, 260], [89, 0.1, 180]);
    box(materials.roof, [x, 18, 336], [83, 36, 92]);
    box(materials.steel, [x, 15, 289.7], [69, 29, 0.7]);
    for (let rib = -4; rib <= 4; rib++) box(materials.roof, [x + rib * 8, 15, 289], [0.5, 29, 0.4]);
    box(materials.glass, [x, 32, 289], [66, 3.5, 0.8]);
    sign(index === 0 ? 'CARGO' : `MRO ${index}`, 57, 14, [x, 36.8, 336]);
    lamp(x - 38, 290, 0xffd69d, 28, 1);
    lamp(x + 38, 290, 0xffd69d, 28, 1);
  }

  // Landside: arrivals road, transit canopy, parking lots, service roads, and canal.
  box(materials.asphalt, [0, -0.015, 550], [1800, 0.11, 32]);
  box(materials.asphalt, [920, -0.015, 190], [32, 0.11, 750]);
  box(materials.asphalt, [-920, -0.015, 100], [28, 0.11, 900]);
  box(materials.asphalt, [0, -0.015, 755], [1800, 0.11, 25]);
  for (let x = -880; x < 900; x += 22) {
    paint(x, 550, 9, 0.4);
    paint(x, 755, 9, 0.3);
  }
  box(materials.roof, [0, 11, 514], [420, 1.2, 21]);
  for (let x = -198; x <= 200; x += 22) box(materials.steel, [x, 5.5, 515], [0.8, 11, 0.8]);
  const carPaint = Array.from({ length: 8 }, (_, index) => standard({
    color: new THREE.Color().setHSL(index / 8, 0.2, 0.21 + index * 0.045), metalness: 0.45, roughness: 0.4,
  }));
  for (const lotX of [-270, 270]) {
    box(materials.asphalt, [lotX, 0, 648], [335, 0.12, 135]);
    for (let row = 0; row < 4; row++) {
      const z = 603 + row * 29;
      for (let slot = 0; slot < 31; slot++) {
        const x = lotX - 151 + slot * 10;
        paint(x, z, 0.16, 15);
        if (random() < 0.66) {
          const car = carPaint[Math.floor(random() * carPaint.length)];
          box(car, [x + 4.5, 1, z], [3.7, 1.8, 7.5]);
          box(materials.glass, [x + 4.5, 2, z - 0.3], [3.1, 1.1, 4]);
        }
      }
    }
  }
  for (let x = -820; x < 880; x += 90) {
    cylinder(materials.steel, [x, 8, 569], [0.25, 16, 0.25]);
    lamp(x, 566, 0xffd39e, 16, 0.75);
  }
  for (let index = 0; index < 7; index++) {
    const group = new THREE.Group();
    box(index % 2 ? materials.white : materials.yellow, [0, 1.1, 0], [7, 1.8, 3], group);
    box(materials.glass, [-0.5, 2.1, 0], [3.8, 1.1, 2.65], group);
    const headlights = resources.material(new THREE.MeshBasicMaterial({ color: new THREE.Color(2.3, 2.1, 1.65) }));
    box(headlights, [3.6, 1, -0.85], [0.12, 0.5, 0.7], group);
    box(headlights, [3.6, 1, 0.85], [0.12, 0.5, 0.7], group);
    group.position.set(index * 247 - 850, 0, index % 2 ? 557 : 542);
    group.rotation.y = index % 2 ? Math.PI : 0;
    scene.add(group);
    dynamicVehicles.push({ group, direction: index % 2 ? -1 : 1, speed: 6 + index * 0.8 });
  }

  box(materials.water, [-1390, -1.7, 0], [145, 2, 3400]);
  box(materials.apron, [-1473, -0.5, 0], [15, 1, 3400]);
  box(materials.apron, [-1307, -0.5, 0], [15, 1, 3400]);
  box(materials.asphalt, [-1390, 4, 755], [220, 4, 27]);

  const city = [];
  for (let index = 0; index < 140; index++) {
    const x = (random() - 0.5) * 4600;
    const z = index < 75 ? -950 - random() * 1200 : 970 + random() * 1400;
    const height = 10 + Math.pow(random(), 2) * 100;
    instance(city, [x, height / 2, z], [25 + random() * 65, height, 20 + random() * 60]);
    for (let y = 8; y < height - 3; y += 8) {
      if (random() > 0.48) instance(windowInstances, [x, y, z + 31], [10 + random() * 13, 1.1, 0.1]);
    }
  }
  batch(boxGeometry, standard({ color: 0x32424c, roughness: 0.88 }), city, true);
  batch(boxGeometry, resources.material(new THREE.MeshBasicMaterial({ color: 0xba9c71 })), windowInstances);

  const trees = [];
  for (let index = 0; index < 440; index++) {
    const x = (random() - 0.5) * 4000;
    const z = (random() - 0.5) * 3200;
    if (Math.abs(x) < 1100 && z > -560 && z < 850 || Math.abs(x + 1390) < 115) continue;
    const size = 3 + random() * 5;
    instance(trees, [x, size * 0.95, z], [size, size * 1.8, size], 0, new THREE.Color().setHSL(0.3, 0.17, 0.15 + random() * 0.1));
  }
  batch(sphereGeometry, standard({ color: 0xffffff, roughness: 1 }), trees, true);

  batch(boxGeometry, materials.white, paintInstances);
  batch(boxGeometry, materials.yellow, yellowInstances);
  batch(boxGeometry, materials.roof, frameInstances, true);
  const lightMaterial = resources.material(new THREE.MeshBasicMaterial({ color: 0xffffff }));
  const lights = batch(sphereGeometry, lightMaterial, lightInstances);

  const poolTexture = canvasTexture(resources, 64, 64, (context, width, height) => {
    const gradient = context.createRadialGradient(width / 2, height / 2, 0, width / 2, height / 2, width / 2);
    gradient.addColorStop(0, 'rgba(135,173,244,0.5)');
    gradient.addColorStop(0.28, 'rgba(113,155,231,0.17)');
    gradient.addColorStop(1, 'rgba(93,134,224,0)');
    context.fillStyle = gradient;
    context.fillRect(0, 0, width, height);
  });
  const poolGeometry = resources.geometry(new THREE.PlaneGeometry(5, 5).rotateX(-Math.PI / 2));
  const poolMaterial = resources.material(new THREE.MeshBasicMaterial({
    map: poolTexture, color: 0x9dafff, transparent: true, opacity: 0.6,
    depthWrite: false, blending: THREE.AdditiveBlending,
  }));
  const pools = batch(poolGeometry, poolMaterial, lightInstances.filter(item => item.position[1] < 2).map(item => ({
    position: [item.position[0], 0.22, item.position[2]], scale: [1, 1, 1],
  })));

  const skyMaterial = resources.material(new THREE.ShaderMaterial({
    side: THREE.BackSide,
    depthWrite: false,
    uniforms: {
      topColor: { value: new THREE.Color('#132c52') },
      horizonColor: { value: new THREE.Color('#e4b18c') },
      bottomColor: { value: new THREE.Color('#5a737d') },
      sunDirection: { value: new THREE.Vector3(-0.75, 0.13, -0.6).normalize() },
      sunColor: { value: new THREE.Color('#ffddae') },
      sunStrength: { value: 0.55 },
    },
    vertexShader: `
      varying vec3 vDirection;
      void main() {
        vDirection = position;
        gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
      }`,
    fragmentShader: `
      varying vec3 vDirection;
      uniform vec3 topColor;
      uniform vec3 horizonColor;
      uniform vec3 bottomColor;
      uniform vec3 sunDirection;
      uniform vec3 sunColor;
      uniform float sunStrength;
      void main() {
        vec3 direction = normalize(vDirection);
        float elevation = max(direction.y, 0.0);
        vec3 sky = mix(horizonColor, topColor, pow(elevation, 0.38));
        sky = mix(sky, bottomColor, 1.0 - smoothstep(-0.15, 0.0, direction.y));
        float alignment = max(dot(direction, sunDirection), 0.0);
        sky += sunColor * (pow(alignment, 18.0) * 0.24 + pow(alignment, 2000.0) * 1.6) * sunStrength;
        gl_FragColor = vec4(sky, 1.0);
        #include <tonemapping_fragment>
        #include <colorspace_fragment>
      }`,
  }));
  const sky = mesh(resources.geometry(new THREE.SphereGeometry(8000, 32, 16)), skyMaterial, [0, 0, 0]);
  sky.castShadow = false;
  sky.receiveShadow = false;
  sky.frustumCulled = false;
  sky.renderOrder = -10;

  const cloudTexture = canvasTexture(resources, 128, 64, (context, width, height) => {
    for (let index = 0; index < 8; index++) {
      const x = 20 + random() * 88;
      const y = 23 + random() * 18;
      const gradient = context.createRadialGradient(x, y, 2, x, y, 24);
      gradient.addColorStop(0, 'rgba(255,255,255,0.4)');
      gradient.addColorStop(1, 'rgba(255,255,255,0)');
      context.fillStyle = gradient;
      context.fillRect(0, 0, width, height);
    }
  });
  const cloudMaterial = resources.material(new THREE.SpriteMaterial({
    map: cloudTexture, color: 0xd5dce2, transparent: true, opacity: 0.19,
    depthWrite: false, fog: true,
  }));
  const clouds = [];
  for (let index = 0; index < 32; index++) {
    const cloud = new THREE.Sprite(cloudMaterial);
    cloud.position.set((random() - 0.5) * 6000, 450 + random() * 240, (random() - 0.5) * 3600 - 800);
    cloud.scale.set(650 + random() * 450, 110 + random() * 100, 1);
    scene.add(cloud);
    clouds.push(cloud);
  }

  const windsock = new THREE.Group();
  cylinder(materials.steel, [392, 6, -60], [0.18, 12, 0.18]);
  windsock.position.set(392, 12, -60);
  const windsockGeometry = resources.geometry(new THREE.CylinderGeometry(0.65, 0.33, 4, 12));
  const sock = mesh(windsockGeometry, standard({ color: 0xe97638, roughness: 1 }), [0, 0, 1.8], null, windsock);
  sock.rotation.x = Math.PI / 2;
  scene.add(windsock);

  // Repeated buildings, bridges, mullions, vehicles, and equipment retain detail without
  // thousands of individual draw calls. Animated objects remain outside these batches.
  const staticBatches = new Map();
  for (const object of [...scene.children]) {
    if (!object.isMesh || object.isInstancedMesh || ![boxGeometry, cylinderGeometry, sphereGeometry].includes(object.geometry)) continue;
    let materialBatches = staticBatches.get(object.geometry);
    if (!materialBatches) staticBatches.set(object.geometry, materialBatches = new Map());
    if (!materialBatches.has(object.material)) materialBatches.set(object.material, new Map());
    const shadowBatches = materialBatches.get(object.material);
    const shadowFlags = `${Number(object.castShadow)}:${Number(object.receiveShadow)}`;
    if (!shadowBatches.has(shadowFlags)) shadowBatches.set(shadowFlags, []);
    shadowBatches.get(shadowFlags).push(object);
  }
  for (const [geometry, materialBatches] of staticBatches) {
    for (const [material, shadowBatches] of materialBatches) {
      for (const objects of shadowBatches.values()) {
        if (objects.length < 2) continue;
        const combined = new THREE.InstancedMesh(geometry, material, objects.length);
        objects.forEach((object, index) => {
          object.updateMatrix();
          combined.setMatrixAt(index, object.matrix);
          scene.remove(object);
        });
        combined.castShadow = objects[0].castShadow;
        combined.receiveShadow = objects[0].receiveShadow;
        scene.add(combined);
      }
    }
  }

  return {
    materials, facadeMaterial, sky, skyMaterial, cloudMaterial, clouds, dynamicVehicles,
    runwayHighlights, landmarkPositions, lampPools, lightMaterial, lights, pools, poolMaterial, windsock,
  };
}
