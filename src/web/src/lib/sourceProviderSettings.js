// Field schema per source provider id. Keys, defaults and types follow the
// yaml contract: every section lives under `virtual_soulfind_v2.backends`.
// Types: bool | int | float | string | secret | list (string[]) | enum.
// Only keys the server maps onto a backend option (and a backend reads) belong
// here: anything else saves fine and then silently does nothing.

const SECTION_BY_PROVIDER = {
  Http: 'http',
  Lan: 'lan',
  LocalLibrary: 'local_library',
  MeshDht: 'mesh_dht',
  NativeMesh: 'native_mesh',
  S3: 's3',
  Soulseek: 'soulseek',
  Torrent: 'torrent',
  WebDav: 'webdav',
};

const field = (key, label, type, defaultValue, description, extra = {}) => ({
  default: defaultValue,
  description,
  key,
  label,
  type,
  ...extra,
});

const MAX_FILE_SIZE = field(
  'max_file_size_bytes',
  'Max file size (bytes)',
  'int',
  500_000_000,
  'Candidates larger than this are rejected before download.',
);

const validationTimeout = (defaultSeconds) => field(
  'validation_timeout_seconds',
  'Validation timeout (s)',
  'int',
  defaultSeconds,
  'How long a HEAD/probe request may take before the candidate is skipped.',
);

const meshFields = [
  field('min_trust_score', 'Min trust score', 'float', 0.3, 'Peers below this trust score (0-1) are ignored.'),
  field('max_candidates_per_item', 'Max candidates per item', 'int', 20, 'Upper bound on candidates collected per wanted item.'),
];

export const PROVIDER_SCHEMAS = {
  Http: [
    field('domain_allowlist', 'Domain allowlist', 'list', [], 'One hostname per line. Only these hosts may be fetched.'),
    MAX_FILE_SIZE,
    validationTimeout(10),
    field('require_https', 'Require HTTPS', 'bool', false, 'Reject plain http:// URLs.'),
  ],
  Lan: [
    field('allowed_networks', 'Allowed networks', 'list', ['192.168.0.0/16', '10.0.0.0/8', '172.16.0.0/12'], 'CIDR ranges, one per line, that LAN shares may live in.'),
    field('allowed_hostnames', 'Allowed hostnames', 'list', [], 'Hostnames, one per line, for UNC paths that are not plain IPs.'),
    field('max_candidates_per_item', 'Max candidates per item', 'int', 10, 'Upper bound on LAN candidates per wanted item.'),
  ],
  LocalLibrary: [],
  MeshDht: meshFields,
  NativeMesh: meshFields,
  S3: [
    field('endpoint', 'Endpoint', 'string', '', 'S3-compatible endpoint URL, e.g. https://s3.example.com.'),
    field('region', 'Region', 'string', '', 'Bucket region, e.g. us-east-1.'),
    field('access_key', 'Access key', 'string', '', 'Access key id used to sign requests.'),
    field('secret_key', 'Secret key', 'secret', '', 'Secret access key. Leave blank to keep the current value.'),
    field('force_path_style', 'Force path style', 'bool', true, 'Use path-style URLs (endpoint/bucket/key) instead of virtual-hosted buckets.'),
    field('bucket_allowlist', 'Bucket allowlist', 'list', [], 'One bucket name per line. Only these buckets may be read.'),
    MAX_FILE_SIZE,
    validationTimeout(15),
  ],
  Soulseek: [
    field('min_upload_speed_bytes_per_sec', 'Min upload speed (B/s)', 'int', 51_200, 'Peers advertising a slower upload speed are skipped.'),
    field('search_timeout_ms', 'Search timeout (ms)', 'int', 10_000, 'Stop collecting search responses after this many milliseconds.'),
  ],
  Torrent: [
    field('min_seeders', 'Min seeders', 'int', 3, 'Torrents with fewer seeders are not considered.'),
    // The torrent engine sets up its DHT node once, from these two, at startup.
    field('private_only', 'Private only', 'bool', true, 'Only accept private torrents.', { restartRequired: true }),
    field('disable_dht', 'Disable DHT', 'bool', true, 'Do not use the public DHT for peer discovery.', { restartRequired: true }),
    field('disable_pex', 'Disable PEX', 'bool', true, 'Do not use peer exchange.'),
    field('allowed_peer_sources', 'Allowed peer sources', 'enum', 'Both', 'Where peers may come from: the mesh overlay, the invite list, or both.', {
      options: ['Overlay', 'InviteList', 'Both'],
    }),
    field('invite_list', 'Invite list', 'list', [], 'One peer address per line that is always allowed.'),
  ],
  WebDav: [
    field('domain_allowlist', 'Domain allowlist', 'list', [], 'One hostname per line. Only these WebDAV hosts may be used.'),
    MAX_FILE_SIZE,
    validationTimeout(15),
    field('username', 'Username', 'string', '', 'Basic-auth username.'),
    field('password', 'Password', 'secret', '', 'Basic-auth password. Leave blank to keep the current value.'),
    field('bearer_token', 'Bearer token', 'secret', '', 'Bearer token used instead of basic auth. Leave blank to keep the current value.'),
  ],
};

export const providerConfigKey = (providerId) =>
  `virtual_soulfind_v2.backends.${SECTION_BY_PROVIDER[providerId] ?? String(providerId ?? '').toLowerCase()}`;

export const getProviderSchema = (providerId) => PROVIDER_SCHEMAS[providerId] ?? [];

const toPath = (configKey) => String(configKey ?? '').split('.').filter(Boolean);

const toJs = (node) => (node && typeof node.toJSON === 'function' ? node.toJSON() : node);

const toLines = (value) => {
  if (Array.isArray(value)) {
    return value.map((entry) => String(entry));
  }

  return [];
};

// Reads a raw value the way the server's config binder does: "true"/"false"
// in any case are booleans, numeric strings are numbers, and enum names ignore
// case (returned in their canonical spelling so they are written back that way).
const bindValue = (definition, raw) => {
  const text = typeof raw === 'string' ? raw.trim() : undefined;

  switch (definition.type) {
    case 'bool':
      return text === undefined ? Boolean(raw) : text.toLowerCase() === 'true';
    case 'int':
    case 'float':
      return text && Number.isFinite(Number(text)) ? Number(text) : raw;
    case 'enum':
      return (definition.options ?? []).find(
        (option) => option.toLowerCase() === String(raw).trim().toLowerCase(),
      ) ?? raw;
    default:
      return raw;
  }
};

// Returns { values, secretsConfigured }. Secrets are never read back: their
// value is always '' and secretsConfigured[key] tells whether one is stored.
export const readProviderSettings = (document, configKey, schema) => {
  const base = toPath(configKey);
  const values = {};
  const secretsConfigured = {};

  for (const definition of schema) {
    const path = [...base, definition.key];
    const raw = document.hasIn(path) ? toJs(document.getIn(path)) : undefined;

    if (definition.type === 'secret') {
      values[definition.key] = '';
      secretsConfigured[definition.key] = raw != null && String(raw) !== '';
    } else if (definition.type === 'list') {
      values[definition.key] = toLines(raw ?? definition.default);
    } else if (raw == null) {
      values[definition.key] = definition.default;
    } else {
      values[definition.key] = bindValue(definition, raw);
    }
  }

  return { secretsConfigured, values };
};

// The server's yaml provider matches keys ignoring case, '_' and '-'; that is
// also how the camelCase names of GET /api/options line up with yaml keys.
const squash = (key) => String(key).replace(/[_-]/gu, '').toLowerCase();

// Wraps the live options (GET /api/options) in the hasIn/getIn shape of a
// yaml document, so readProviderSettings reads the values the server bound.
export const liveOptionsDocument = (options) => {
  const getIn = (path) => path.reduce((node, segment) => {
    if (!node || typeof node !== 'object') {
      return undefined;
    }

    const match = Object.keys(node).find((key) => squash(key) === squash(segment));

    return match === undefined ? undefined : node[match];
  }, options);

  return { getIn, hasIn: (path) => getIn(path) != null };
};

// True once the live options hold every value `document` sets for this
// provider. Secrets come back redacted and removed keys fall back to server
// defaults, so only the non-secret keys the document sets are compared.
export const liveSettingsMatch = (options, document, configKey, schema) => {
  const base = toPath(configKey);
  const comparable = schema.filter(
    (definition) => definition.type !== 'secret' && document.hasIn([...base, definition.key]),
  );
  const saved = readProviderSettings(document, configKey, comparable).values;
  const live = readProviderSettings(liveOptionsDocument(options), configKey, comparable).values;

  // The server binds floats in single precision.
  return comparable.every(({ key, type }) => (
    type === 'float'
      ? Math.fround(saved[key]) === Math.fround(live[key])
      : JSON.stringify(saved[key]) === JSON.stringify(live[key])
  ));
};

// deleteIn throws when an intermediate map is missing; only delete what exists.
const removeKey = (document, path) => {
  if (document.hasIn(path)) {
    document.deleteIn(path);
  }
};

const parseNumber = (value, isInt) => {
  const text = String(value ?? '').trim();

  if (!text) {
    return null;
  }

  const number = isInt ? Number.parseInt(text, 10) : Number.parseFloat(text);

  return Number.isFinite(number) ? number : null;
};

// A blank secret stays untouched and a null one ("Clear stored value") is
// removed; empty lists/strings/numbers remove the key so the backend default
// applies again.
export const writeProviderSettings = (document, configKey, values, schema) => {
  const base = toPath(configKey);

  for (const definition of schema) {
    const path = [...base, definition.key];
    const value = values[definition.key];

    switch (definition.type) {
      case 'bool':
        document.setIn(path, Boolean(value));
        break;
      case 'int':
      case 'float': {
        const number = parseNumber(value, definition.type === 'int');

        if (number == null) {
          removeKey(document, path);
        } else {
          document.setIn(path, number);
        }

        break;
      }

      case 'list': {
        const lines = (Array.isArray(value) ? value : String(value ?? '').split(/\r?\n/u))
          .map((line) => String(line).trim())
          .filter(Boolean);

        if (lines.length === 0) {
          removeKey(document, path);
        } else {
          document.setIn(path, lines);
        }

        break;
      }

      case 'secret': {
        const text = String(value ?? '');

        if (value === null) {
          removeKey(document, path);
        } else if (text.trim()) {
          document.setIn(path, text);
        }

        break;
      }

      default: {
        const text = String(value ?? '').trim();

        if (text) {
          document.setIn(path, text);
        } else {
          removeKey(document, path);
        }
      }
    }
  }

  return document;
};

export const setProviderEnabled = (document, configKey, enabled) => {
  document.setIn([...toPath(configKey), 'enabled'], Boolean(enabled));

  return document;
};
