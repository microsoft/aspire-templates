import { useEffect, useRef, useState, type RefObject } from "react";
import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import { customerLabel, shortId, statusLabel, type Order } from "./types";

type Props = { orders: Order[]; selected: string | null; offset: RefObject<number>; online: boolean; onSelect: (id: string) => void; onService: (name: string) => void };
type Truck = { group: THREE.Group; marker: HTMLButtonElement; order: Order; progress: number };
const homes: Record<string, [number, number, number]> = { "harbor-house": [8, 0, 5], "garden-house": [3, 0, 6], "hill-house": [-2, 0, 6] };

export function District(props: Props) {
  const host = useRef<HTMLDivElement>(null);
  const live = useRef(props);
  const [failure, setFailure] = useState("");
  const [paused, setPaused] = useState(false);
  const pause = useRef(false);
  live.current = props; pause.current = paused;
  useEffect(() => {
    const element = host.current!;
    let renderer: THREE.WebGLRenderer;
    try { renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true }); }
    catch { setFailure("3D rendering is unavailable on this device. The accessible order controls and event history below remain fully functional."); return; }
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.shadowMap.enabled = true; renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    renderer.setClearColor(0, 0);
    element.appendChild(renderer.domElement);
    renderer.domElement.setAttribute("aria-label", "Isometric commerce district: storefront, payments, warehouse, shipping depot and three customer houses.");
    renderer.domElement.setAttribute("role", "img");
    const css = getComputedStyle(document.documentElement);
    const colors = (name: string) => new THREE.Color(css.getPropertyValue(`--cp-${name}`).trim());
    const materials = new Map<string, THREE.MeshStandardMaterial>();
    function material(name: string) {
      if (!materials.has(name)) materials.set(name, new THREE.MeshStandardMaterial({ color: colors(name), roughness: 0.74, metalness: 0.08 }));
      return materials.get(name)!;
    }
    const scene = new THREE.Scene();
    const camera = new THREE.OrthographicCamera(-16, 16, 11, -11, 0.1, 150);
    camera.position.set(23, 24, 28); camera.lookAt(0, 0, 0);
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true; controls.enablePan = false; controls.minZoom = 0.65; controls.maxZoom = 1.7;
    controls.minPolarAngle = Math.PI / 7; controls.maxPolarAngle = Math.PI / 2.7;
    scene.add(new THREE.AmbientLight(colors("surface"), 2.4));
    const sun = new THREE.DirectionalLight(colors("surface"), 4);
    sun.position.set(-8, 22, 12); sun.castShadow = true; sun.shadow.mapSize.set(2048, 2048);
    Object.assign(sun.shadow.camera, { left: -20, right: 20, top: 20, bottom: -20 }); sun.shadow.bias = -0.001;
    scene.add(sun);
    const geometries: THREE.BufferGeometry[] = [];
    function box(parent: THREE.Object3D, x: number, y: number, z: number, w: number, h: number, d: number, color: string) {
      const geometry = new THREE.BoxGeometry(w, h, d); geometries.push(geometry);
      const mesh = new THREE.Mesh(geometry, material(color)); mesh.position.set(x, y, z); mesh.castShadow = true; mesh.receiveShadow = true; parent.add(mesh); return mesh;
    }
    function cylinder(parent: THREE.Object3D, x: number, y: number, z: number, radius: number, height: number, color: string, sides = 12) {
      const geometry = new THREE.CylinderGeometry(radius, radius, height, sides); geometries.push(geometry);
      const mesh = new THREE.Mesh(geometry, material(color)); mesh.position.set(x, y, z); mesh.castShadow = true; mesh.receiveShadow = true; parent.add(mesh); return mesh;
    }
    const base = box(scene, 0, -0.7, 0, 25, 1, 19, "bg-elevated");
    box(scene, 0, -0.16, 0, 25.2, 0.12, 19.2, "border");
    box(scene, 0, -0.06, 1.4, 23.5, 0.12, 2.3, "text-muted");
    box(scene, 6.5, -0.06, 0, 2.3, 0.12, 15, "text-muted");
    box(scene, 0, -0.055, 4.15, 23, 0.1, 0.48, "surface-soft");
    for (let x = -11; x < 12; x += 2) box(scene, x, 0.025, 1.4, 0.8, 0.02, 0.055, "surface");
    for (let z = -7; z < 8; z += 2) box(scene, 6.5, 0.025, z, 0.055, 0.02, 0.8, "surface");
    const labels: { node: HTMLButtonElement; point: THREE.Vector3 }[] = [];
    function label(text: string, point: [number, number, number], action: () => void, kind = "") {
      const node = document.createElement("button"); node.className = `scene-label ${kind}`; node.textContent = text; node.onclick = action; element.appendChild(node);
      labels.push({ node, point: new THREE.Vector3(...point) });
      return node;
    }
    function building(name: string, x: number, z: number, width: number, depth: number, height: number, accent = "accent") {
      const group = new THREE.Group(); group.position.set(x, 0, z); scene.add(group);
      box(group, 0, 0.1, 0, width + 0.6, 0.3, depth + 0.6, "surface-soft");
      box(group, 0, height / 2 + 0.2, 0, width, height, depth, "surface");
      box(group, 0, height + 0.3, 0, width + 0.25, 0.25, depth + 0.25, accent);
      box(group, 0, height * 0.6, depth / 2 + 0.025, width * 0.78, height * 0.5, 0.06, "link");
      for (let i = -1; i <= 1; i++) box(group, i * width * 0.27, height * 0.6, depth / 2 + 0.07, 0.07, height * 0.55, 0.06, "surface");
      label(name, [x, height + 1.25, z], () => live.current.onService(name.startsWith("01") ? "ordering" : name.startsWith("02") ? "payments" : name.startsWith("03") ? "fulfilment" : "shipping"));
      return group;
    }
    const shop = building("01 / Storefront", -7.5, -0.6, 3.8, 2.4, 2.4);
    for (let i = 0; i < 7; i++) box(shop, -1.65 + i * 0.55, 2.05, 1.65, 0.55, 0.13, 1.05, i % 2 ? "surface" : "accent");
    box(shop, 1.1, 0.75, 1.25, 0.6, 1.4, 0.08, "text-muted");
    building("02 / Mock payments", -7, -5.8, 3, 2.3, 1.7, "link");
    const terminal = box(scene, -6.9, 2.7, -5.8, 0.8, 1.1, 0.25, "surface");
    box(terminal, 0, 0.1, 0.15, 0.55, 0.65, 0.04, "success");
    const warehouse = building("03 / Fulfilment", -0.4, -4.8, 5, 4, 2.5, "warning");
    box(warehouse, 0, 1.05, 2.07, 2, 1.7, 0.08, "text-muted");
    for (let i = 0; i < 5; i++) box(warehouse, 0, 0.35 + i * 0.31, 2.13, 1.9, 0.05, 0.05, "border-strong");
    box(scene, -0.4, 0.6, -0.8, 1.4, 0.23, 3, "text-muted");
    for (let i = 0; i < 8; i++) {
      const roller = cylinder(scene, -0.4, 0.76, -2 + i * 0.35, 0.08, 1.25, "border-strong");
      roller.rotation.z = Math.PI / 2;
    }
    const parcel = box(scene, -0.4, 1.05, -1.9, 0.6, 0.55, 0.55, "warning");
    box(parcel, 0, 0.28, 0, 0.12, 0.015, 0.56, "surface");
    const depot = building("04 / Shipping depot", 4, -5.3, 2.9, 3.3, 1.65, "success");
    box(depot, 0, 0.9, 1.71, 2.2, 1.45, 0.1, "text-muted");
    for (let x = 2.7; x < 5.3; x += 0.8) {
      box(scene, x, 0.4, -2.9, 0.65, 0.75, 0.6, "warning");
      box(scene, x, 0.79, -2.9, 0.12, 0.03, 0.62, "surface");
    }
    for (const [id, point] of Object.entries(homes)) {
      const [x, , z] = point;
      box(scene, x, 0.03, z + 0.3, 3.1, 0.14, 3.2, "success");
      box(scene, x, 0.8, z, 1.9, 1.5, 1.8, "surface");
      const geometry = new THREE.ConeGeometry(1.65, 1.1, 4); geometries.push(geometry);
      const roof = new THREE.Mesh(geometry, material("accent")); roof.position.set(x, 2.05, z); roof.rotation.y = Math.PI / 4; roof.castShadow = true; scene.add(roof);
      box(scene, x + 0.35, 0.57, z - 0.94, 0.45, 0.95, 0.1, "text-muted");
      box(scene, x - 0.4, 0.95, z - 0.94, 0.5, 0.55, 0.1, "link");
      label(customerLabel(id), [x, 3.05, z], () => {
        const order = live.current.orders.find(o => o.customerId === id);
        if (order) live.current.onSelect(order.id);
      }, "home-label");
    }
    for (const [x, z] of [[-11, -6], [-11, -3], [-11, 5], [-8, 6], [0, -8], [9, -6], [10, 0], [10, 7], [0, 8]]) {
      cylinder(scene, x, 0.7, z, 0.12, 1.4, "text-soft");
      const geometry = new THREE.IcosahedronGeometry(0.8, 0); geometries.push(geometry);
      const crown = new THREE.Mesh(geometry, material("success")); crown.position.set(x, 1.7, z); crown.castShadow = true; scene.add(crown);
    }
    for (const x of [-10, -4, 3, 10]) {
      cylinder(scene, x, 1.1, 2.9, 0.06, 2.2, "border-strong");
      box(scene, x, 2.25, 2.7, 0.3, 0.15, 0.5, "surface");
    }
    const links: THREE.CatmullRomCurve3[] = [];
    const particles: THREE.Mesh[] = [];
    const stationPoints = [[-7.5, 2.8, -0.6], [-7, 2.5, -5.8], [-0.4, 3.4, -4.8], [4, 2.5, -5.3]];
    for (let i = 0; i < 3; i++) {
      const a = new THREE.Vector3(...stationPoints[i]); const b = new THREE.Vector3(...stationPoints[i + 1]);
      const mid = a.clone().lerp(b, 0.5); mid.y += 1.7;
      const curve = new THREE.CatmullRomCurve3([a, mid, b]); links.push(curve);
      const geometry = new THREE.TubeGeometry(curve, 32, 0.025, 5, false); geometries.push(geometry);
      scene.add(new THREE.Mesh(geometry, material("border-strong")));
      const sphere = new THREE.SphereGeometry(0.15, 10, 8); geometries.push(sphere);
      const dot = new THREE.Mesh(sphere, material("accent")); scene.add(dot); particles.push(dot);
    }
    const trucks = new Map<string, Truck>();
    function createTruck(order: Order) {
      const group = new THREE.Group(); scene.add(group);
      box(group, 0, 0.5, 0, 1.55, 0.18, 0.78, "text-muted");
      box(group, -0.2, 1, 0, 1.15, 0.95, 0.86, "surface");
      box(group, 0.7, 0.85, 0, 0.65, 0.65, 0.8, "accent");
      box(group, 0.76, 1.06, 0, 0.35, 0.23, 0.82, "link");
      box(group, -0.2, 1.49, 0, 0.18, 0.03, 0.87, "accent");
      for (const x of [-0.55, 0.65]) for (const z of [-0.43, 0.43]) {
        const wheel = cylinder(group, x, 0.38, z, 0.23, 0.13, "text"); wheel.rotation.x = Math.PI / 2;
      }
      const marker = document.createElement("button"); marker.className = "truck-label"; marker.textContent = `#${shortId(order.id)}`; marker.onclick = () => live.current.onSelect(order.id); element.appendChild(marker);
      const truck = { group, marker, order, progress: 0 }; trucks.set(order.id, truck); return truck;
    }
    function resize() {
      const width = element.clientWidth; const height = element.clientHeight;
      renderer.setSize(width, height);
      camera.left = -14 * width / height; camera.right = 14 * width / height; camera.top = 14; camera.bottom = -14; camera.updateProjectionMatrix();
    }
    const observer = new ResizeObserver(resize); observer.observe(element); resize();
    const motion = window.matchMedia("(prefers-reduced-motion: reduce)");
    let frame = 0;
    const clock = new THREE.Clock();
    function positionLabel(node: HTMLElement, point: THREE.Vector3) {
      const screen = point.clone().project(camera);
      node.style.left = `${(screen.x + 1) / 2 * element.clientWidth}px`;
      node.style.top = `${(-screen.y + 1) / 2 * element.clientHeight}px`;
    }
    const path = [new THREE.Vector3(4.2, 0, -2.1), new THREE.Vector3(6.5, 0, -2.1), new THREE.Vector3(6.5, 0, 1.4)];
    function animate() {
      frame = requestAnimationFrame(animate);
      const elapsed = clock.getElapsedTime();
      const { orders, selected, online, offset } = live.current;
      const moving = online && !pause.current && !motion.matches;
      controls.update();
      parcel.visible = orders.some(o => o.status === "Picking" || o.status === "Packed");
      if (moving) parcel.position.z = -1.9 + (elapsed % 3) / 3 * 2.1;
      for (let i = 0; i < particles.length; i++) {
        const relevant = orders.some(o => i === 0 ? ["PaymentPending", "PaymentRetryScheduled"].includes(o.status)
          : i === 1 ? ["PaymentAuthorized", "Picking"].includes(o.status) : o.status === "Packed");
        particles[i].visible = relevant;
        particles[i].position.copy(links[i].getPoint(moving ? (elapsed * 0.35 + i * 0.2) % 1 : 0.5));
      }
      const visibleOrders = orders.filter(o => ["InTransit", "Delivered"].includes(o.status))
        .sort((a, b) => Number(b.id === selected) - Number(a.id === selected)
          || Number(b.status === "InTransit") - Number(a.status === "InTransit")).slice(0, 24);
      for (const [id, truck] of trucks) if (!visibleOrders.some(o => o.id === id)) {
        scene.remove(truck.group); truck.marker.remove(); trucks.delete(id);
        truck.group.traverse(object => { if (object instanceof THREE.Mesh) { object.geometry.dispose(); const index = geometries.indexOf(object.geometry); if (index >= 0) geometries.splice(index, 1); } });
      }
      for (let i = 0; i < visibleOrders.length; i++) {
        const order = visibleOrders[i]; const truck = trucks.get(order.id) ?? createTruck(order); truck.order = order;
        const home = homes[order.customerId];
        const route = [...path, new THREE.Vector3(home[0], 0, 1.4), new THREE.Vector3(home[0], 0, home[2] - 1.8)];
        const curve = new THREE.CatmullRomCurve3(route, false, "centripetal");
        const duration = Date.parse(order.nextAt ?? order.updatedAt) - Date.parse(order.updatedAt);
        const time = Date.now() + offset.current - Date.parse(order.updatedAt);
        if (order.status === "Delivered") truck.progress = 1;
        else if (online && !pause.current) truck.progress = motion.matches ? 0.5 : Math.max(0, Math.min(0.94, time / Math.max(1, duration)));
        const progress = truck.progress;
        // Arrival is reserved for a committed Delivered event, never a client timer.
        truck.group.position.copy(curve.getPoint(progress));
        truck.group.position.z += (i % 3 - 1) * 0.16;
        const direction = curve.getTangent(Math.min(progress, 0.999));
        truck.group.rotation.y = -Math.atan2(direction.z, direction.x);
        truck.group.scale.setScalar(selected === order.id ? 1.1 : 0.82);
        truck.marker.classList.toggle("selected", selected === order.id);
        truck.marker.title = `${shortId(order.id)}: ${statusLabel(order.status)} to ${customerLabel(order.customerId)}`;
        positionLabel(truck.marker, truck.group.position.clone().add(new THREE.Vector3(0, 2.1, 0)));
      }
      for (const item of labels) positionLabel(item.node, item.point);
      base.rotation.y = 0;
      renderer.render(scene, camera);
    }
    const lost = (event: Event) => { event.preventDefault(); setFailure("The 3D context was interrupted. Reload to restore the scene; order controls and durable history are still available."); };
    renderer.domElement.addEventListener("webglcontextlost", lost);
    animate();
    return () => {
      cancelAnimationFrame(frame); observer.disconnect(); controls.dispose();
      renderer.domElement.removeEventListener("webglcontextlost", lost);
      for (const geometry of geometries) geometry.dispose();
      for (const mat of materials.values()) mat.dispose();
      renderer.dispose(); element.replaceChildren();
    };
  }, []);
  return <div className="scene-shell"><div ref={host} className="scene" />
    {failure && <div className="scene-failure" role="alert"><h3>The district is in accessible mode.</h3><p>{failure}</p></div>}
    <button className="motion-toggle" onClick={() => setPaused(!paused)} aria-pressed={paused}>{paused ? "Resume motion" : "Pause motion"}</button>
    <div className="scene-caption"><span className="caption-line" /><b>From checkout to doorstep.</b><span>Click a service to look inside.</span></div>
  </div>;
}
