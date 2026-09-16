// Vue 3D exploratoire de la carte réseau (Phase 3) : reprend les mêmes données que la vue 2D (nœud central CIT +
// un nœud par serveur distant, disposés en cercle), avec des particules animées sur les connexions actuellement
// actives. Three.js + OrbitControls sont vendored localement (wwwroot/js/vendor, aucun appel réseau externe requis
// — utile sur un serveur CIT sans accès internet) et chargés à la demande, uniquement quand l'utilisateur bascule
// en 3D, pour ne pas alourdir le reste de l'application.
window.kernelMK = window.kernelMK || {};
window.kernelMK.reseau3d = (function () {
    let scene, camera, renderer, controls, group, animationId, container, resizeObserver;
    let libsLoadingPromise = null;

    // Beaucoup d'environnements distants (session RDP/Bureau à distance sans accélération graphique GPU,
    // machine virtuelle sans pilote GPU) désactivent ou n'exposent pas WebGL — dans ce cas, three.js charge
    // correctement mais la création du renderer échoue. Détecté ici pour donner un message exact plutôt que
    // de laisser croire à un problème de fichiers manquants.
    function isWebGLAvailable() {
        try {
            const canvas = document.createElement('canvas');
            return !!(window.WebGLRenderingContext && (canvas.getContext('webgl2') || canvas.getContext('webgl')));
        } catch (e) {
            return false;
        }
    }

    function loadScript(src) {
        return new Promise(function (resolve, reject) {
            const script = document.createElement('script');
            script.src = src;
            script.onload = resolve;
            script.onerror = reject;
            document.head.appendChild(script);
        });
    }

    function loadLibs() {
        if (window.THREE && window.THREE.OrbitControls) return Promise.resolve();
        if (libsLoadingPromise) return libsLoadingPromise;
        libsLoadingPromise = (window.THREE ? Promise.resolve() : loadScript('js/vendor/three.min.js'))
            .then(function () { return loadScript('js/vendor/OrbitControls.js'); });
        return libsLoadingPromise;
    }

    // Étiquette texte "toujours face à la caméra" (sprite avec texture canvas) : lisible sous n'importe quel angle,
    // contrairement à un texte plaqué sur la géométrie qui deviendrait illisible pendant la rotation/l'orbite.
    function makeTextSprite(text, opts) {
        const THREE = window.THREE;
        opts = opts || {};
        const fontSize = opts.fontSize || 34;
        const color = opts.color || '#ffffff';
        const weight = opts.weight || '700';

        const canvas = document.createElement('canvas');
        const ctx = canvas.getContext('2d');
        ctx.font = weight + ' ' + fontSize + 'px system-ui, -apple-system, Segoe UI, sans-serif';
        const textWidth = ctx.measureText(text).width;
        canvas.width = Math.ceil(textWidth) + 24;
        canvas.height = fontSize + 20;

        ctx.font = weight + ' ' + fontSize + 'px system-ui, -apple-system, Segoe UI, sans-serif';
        ctx.fillStyle = 'rgba(15,23,42,0.72)';
        ctx.beginPath();
        ctx.roundRect(0, 0, canvas.width, canvas.height, 8);
        ctx.fill();
        ctx.fillStyle = color;
        ctx.textBaseline = 'middle';
        ctx.fillText(text, 12, canvas.height / 2 + 2);

        const texture = new THREE.CanvasTexture(canvas);
        texture.minFilter = THREE.LinearFilter;
        const material = new THREE.SpriteMaterial({ map: texture, transparent: true, depthTest: false });
        const sprite = new THREE.Sprite(material);
        const scale = (opts.scale || 0.013);
        sprite.scale.set(canvas.width * scale, canvas.height * scale, 1);
        return sprite;
    }

    function buildScene(containerId, nodes, actives) {
        const THREE = window.THREE;
        container = document.getElementById(containerId);
        if (!container) return;
        container.innerHTML = '';

        const width = container.clientWidth || 800;
        const height = container.clientHeight || 480;

        scene = new THREE.Scene();
        scene.background = new THREE.Color(0x0b1120);
        scene.fog = new THREE.Fog(0x0b1120, 18, 42);

        const radius = Math.max(6, nodes.length * 1.15);

        camera = new THREE.PerspectiveCamera(50, width / height, 0.1, 1000);
        camera.position.set(0, radius * 0.55, radius * 1.35);

        renderer = new THREE.WebGLRenderer({ antialias: true });
        renderer.setSize(width, height);
        renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
        container.appendChild(renderer.domElement);

        controls = new THREE.OrbitControls(camera, renderer.domElement);
        controls.target.set(0, 0, 0);
        controls.enableDamping = true;
        controls.dampingFactor = 0.08;
        controls.autoRotate = true;
        controls.autoRotateSpeed = 0.6;
        controls.minDistance = 4;
        controls.maxDistance = 60;
        controls.update();

        scene.add(new THREE.AmbientLight(0xffffff, 0.65));
        const point = new THREE.PointLight(0xffffff, 0.7);
        point.position.set(10, 14, 10);
        scene.add(point);

        // Grille au sol pour donner un repère spatial (profondeur, échelle) dans la scène 3D.
        const grid = new THREE.GridHelper(radius * 2.6, 20, 0x1e293b, 0x1e293b);
        grid.position.y = -1.4;
        grid.material.transparent = true;
        grid.material.opacity = 0.35;
        scene.add(grid);

        group = new THREE.Group();
        group.userData.particles = [];
        group.userData.pulsingMeshes = [];
        scene.add(group);

        // Nœud central CIT
        const hub = new THREE.Mesh(new THREE.SphereGeometry(1, 24, 24), new THREE.MeshStandardMaterial({ color: 0x4f46e5, emissive: 0x312e81, emissiveIntensity: 0.4 }));
        group.add(hub);
        const hubLabel = makeTextSprite('CIT (Local)', { color: '#c7d2fe', fontSize: 24 });
        hubLabel.position.set(0, 1.3, 0);
        group.add(hubLabel);

        // Nœuds en forme de conteneur (clin d'œil au terminal à conteneurs CIT), une couleur par partenaire
        // façon coques d'armateurs, avec un liseré sombre pour l'effet "tôle ondulée" cartoon.
        const palette = [0x0f52ba, 0xe4032e, 0xf7b500, 0xff6600, 0x00843d, 0x6b21a8, 0x0d9488, 0xdb2777];
        const nodeGeo = new THREE.BoxGeometry(1.3, 0.9, 0.75);
        const edgesGeo = new THREE.EdgesGeometry(nodeGeo);

        nodes.forEach(function (node, i) {
            const angle = (2 * Math.PI * i) / Math.max(1, nodes.length);
            const x = radius * Math.cos(angle);
            const z = radius * Math.sin(angle);

            const transfers = actives.filter(function (a) { return a.host === node.host; });
            const isActive = transfers.length > 0;
            const baseColor = palette[i % palette.length];

            const mat = new THREE.MeshStandardMaterial({
                color: baseColor,
                emissive: baseColor,
                emissiveIntensity: isActive ? 0.35 : 0,
                metalness: 0.25,
                roughness: 0.55
            });
            const mesh = new THREE.Mesh(nodeGeo, mat);
            mesh.position.set(x, 0, z);
            mesh.rotation.y = -angle;
            mesh.userData.pulsing = isActive;
            mesh.userData.baseEmissive = isActive ? 0.35 : 0;
            group.add(mesh);

            const edges = new THREE.LineSegments(edgesGeo, new THREE.LineBasicMaterial({ color: 0x0b1120 }));
            edges.position.copy(mesh.position);
            edges.rotation.copy(mesh.rotation);
            group.add(edges);
            if (isActive) group.userData.pulsingMeshes.push(mesh);

            const labelText = node.name ? (node.host + '  ·  ' + node.name) : node.host;
            const label = makeTextSprite(labelText, { color: isActive ? '#c7d2fe' : '#cbd5e1', fontSize: 22 });
            label.position.set(x, 0.85, z);
            group.add(label);

            if (isActive) {
                const activityText = transfers.map(function (t) { return (t.upload ? '↑ ' : '↓ ') + t.jobName; }).join('  ·  ');
                const activityLabel = makeTextSprite(activityText, { color: '#fbbf24', fontSize: 20 });
                activityLabel.position.set(x, 1.3, z);
                group.add(activityLabel);
            }

            const lineMat = new THREE.LineBasicMaterial({ color: isActive ? baseColor : 0x334155, transparent: true, opacity: isActive ? 0.9 : 0.3 });
            const lineGeo = new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(0, 0, 0), new THREE.Vector3(x, 0, z)]);
            group.add(new THREE.Line(lineGeo, lineMat));

            transfers.forEach(function (a) {
                const glow = new THREE.Sprite(new THREE.SpriteMaterial({ color: 0xfbbf24, transparent: true, opacity: 0.35, depthTest: false }));
                glow.scale.set(0.9, 0.9, 1);
                const particle = new THREE.Mesh(
                    new THREE.SphereGeometry(0.24, 14, 14),
                    new THREE.MeshBasicMaterial({ color: 0xfbbf24 })
                );
                particle.add(glow);
                particle.userData = {
                    from: a.upload ? new THREE.Vector3(0, 0, 0) : new THREE.Vector3(x, 0, z),
                    to: a.upload ? new THREE.Vector3(x, 0, z) : new THREE.Vector3(0, 0, 0),
                    t: Math.random()
                };
                group.add(particle);
                group.userData.particles.push(particle);
            });
        });

        // Suit la taille réelle du conteneur (redimensionnement de fenêtre / panneau) plutôt qu'une taille figée.
        if (resizeObserver) resizeObserver.disconnect();
        resizeObserver = new ResizeObserver(function () {
            if (!renderer || !camera || !container) return;
            const w = container.clientWidth || width;
            const h = container.clientHeight || height;
            camera.aspect = w / h;
            camera.updateProjectionMatrix();
            renderer.setSize(w, h);
        });
        resizeObserver.observe(container);
    }

    function animate() {
        animationId = requestAnimationFrame(animate);
        if (controls) controls.update();
        if (group) {
            (group.userData.particles || []).forEach(function (p) {
                p.userData.t += 0.01;
                if (p.userData.t > 1) p.userData.t = 0;
                p.position.lerpVectors(p.userData.from, p.userData.to, p.userData.t);
            });
            // Petit effet de respiration lumineuse sur les conteneurs actifs, pour un rendu plus vivant.
            const pulse = 0.25 + 0.25 * Math.sin(Date.now() / 300);
            (group.userData.pulsingMeshes || []).forEach(function (m) {
                m.material.emissiveIntensity = m.userData.baseEmissive + pulse;
            });
        }
        if (renderer && scene && camera) renderer.render(scene, camera);
    }

    return {
        init: function (containerId, nodes, actives) {
            if (!isWebGLAvailable()) {
                const el = document.getElementById(containerId);
                if (el) el.innerHTML = '<div style="color:#fbbf24;padding:16px;font-size:13px;line-height:1.5;">⚠️ La vue 3D nécessite l\'accélération graphique WebGL, indisponible dans ce navigateur/cette session (fréquent via une connexion Bureau à distance / RDP sans GPU). La vue 2D reste pleinement fonctionnelle — utilise le bouton « Vue 2D » ci-dessus, ou ouvre cette page depuis un poste local avec un navigateur récent pour la vue 3D.</div>';
                return Promise.resolve();
            }
            return loadLibs().then(function () {
                buildScene(containerId, nodes, actives);
                if (!animationId) animate();
            }).catch(function (err) {
                console.error('Impossible de charger la bibliothèque 3D :', err);
                const el = document.getElementById(containerId);
                if (el) el.innerHTML = '<p style="color:#f87171;padding:16px;font-size:13px;">Impossible de charger la bibliothèque 3D (fichiers js/vendor/three.min.js ou OrbitControls.js introuvables/corrompus, ou erreur d\'initialisation graphique).</p>';
            });
        },
        update: function (nodes, actives) {
            if (!window.THREE || !container) return;
            const savedPosition = camera.position.clone();
            const savedTarget = controls.target.clone();
            buildScene(container.id, nodes, actives);
            camera.position.copy(savedPosition);
            controls.target.copy(savedTarget);
            controls.update();
        },
        dispose: function () {
            if (animationId) { cancelAnimationFrame(animationId); animationId = null; }
            if (resizeObserver) { resizeObserver.disconnect(); resizeObserver = null; }
            if (controls) { controls.dispose(); controls = null; }
            if (renderer) { renderer.dispose(); renderer = null; }
            if (container) { container.innerHTML = ''; }
            scene = null; camera = null; group = null; container = null;
        }
    };
})();
