import {
  getProviderSchema,
  liveOptionsDocument,
  liveSettingsMatch,
  providerConfigKey,
  readProviderSettings,
  setProviderEnabled,
  writeProviderSettings,
} from './sourceProviderSettings';
import { describe, expect, it } from 'vitest';
import YAML from 'yaml';

const HTTP_KEY = 'virtual_soulfind_v2.backends.http';
const keysOf = (providerId) => getProviderSchema(providerId).map((definition) => definition.key);

describe('sourceProviderSettings', () => {
  it('maps provider ids to the contract yaml sections', () => {
    expect(providerConfigKey('WebDav')).toBe('virtual_soulfind_v2.backends.webdav');
    expect(providerConfigKey('LocalLibrary')).toBe('virtual_soulfind_v2.backends.local_library');
    expect(providerConfigKey('MeshDht')).toBe('virtual_soulfind_v2.backends.mesh_dht');
    expect(providerConfigKey('S3')).toBe('virtual_soulfind_v2.backends.s3');
  });

  it('offers only settings the server maps onto a backend option', () => {
    // Rate limits, request timeout, parallel searches and the mesh query
    // timeout bind to options nothing reads, so the dialog must not offer them.
    expect(keysOf('Http')).toEqual([
      'domain_allowlist',
      'max_file_size_bytes',
      'validation_timeout_seconds',
      'require_https',
    ]);
    expect(keysOf('Soulseek')).toEqual(['min_upload_speed_bytes_per_sec', 'search_timeout_ms']);
    expect(keysOf('NativeMesh')).toEqual(['min_trust_score', 'max_candidates_per_item']);
    expect(keysOf('MeshDht')).toEqual(['min_trust_score', 'max_candidates_per_item']);
  });

  it('marks the torrent settings the engine only reads at startup', () => {
    const restartRequired = getProviderSchema('Torrent')
      .filter((definition) => definition.restartRequired)
      .map((definition) => definition.key);

    expect(restartRequired).toEqual(['private_only', 'disable_dht']);
  });

  it('reads defaults when the section is missing', () => {
    const document = YAML.parseDocument('web: {}\n');
    const { values } = readProviderSettings(document, HTTP_KEY, getProviderSchema('Http'));

    expect(values).toEqual({
      domain_allowlist: [],
      max_file_size_bytes: 500_000_000,
      require_https: false,
      validation_timeout_seconds: 10,
    });
  });

  it('reads stored values and reports configured secrets without exposing them', () => {
    const document = YAML.parseDocument([
      'virtual_soulfind_v2:',
      '  backends:',
      '    webdav:',
      '      enabled: true',
      '      domain_allowlist: [dav.example.com]',
      '      password: hunter2',
    ].join('\n'));
    const { secretsConfigured, values } = readProviderSettings(
      document,
      providerConfigKey('WebDav'),
      getProviderSchema('WebDav'),
    );

    expect(values.domain_allowlist).toEqual(['dav.example.com']);
    expect(values.password).toBe('');
    expect(values.bearer_token).toBe('');
    expect(secretsConfigured).toEqual({ bearer_token: false, password: true });
  });

  it('reads quoted booleans, numeric strings and enum casing the way the server binds them', () => {
    const document = YAML.parseDocument([
      'virtual_soulfind_v2:',
      '  backends:',
      '    s3:',
      '      force_path_style: "false"',
      '      max_file_size_bytes: "1000"',
      '    torrent:',
      '      private_only: "FALSE"',
      '      disable_dht: " True "',
      '      allowed_peer_sources: both',
    ].join('\n'));
    const s3 = readProviderSettings(document, providerConfigKey('S3'), getProviderSchema('S3')).values;
    const torrent = readProviderSettings(document, providerConfigKey('Torrent'), getProviderSchema('Torrent')).values;

    expect(s3.force_path_style).toBe(false);
    expect(s3.max_file_size_bytes).toBe(1000);
    expect(torrent.private_only).toBe(false);
    expect(torrent.disable_dht).toBe(true);
    expect(torrent.allowed_peer_sources).toBe('Both');

    // Saving the dialog unchanged keeps the meaning and the canonical spelling.
    writeProviderSettings(document, providerConfigKey('S3'), s3, getProviderSchema('S3'));
    writeProviderSettings(document, providerConfigKey('Torrent'), torrent, getProviderSchema('Torrent'));
    const backends = document.toJS().virtual_soulfind_v2.backends;
    expect(backends.s3.force_path_style).toBe(false);
    expect(backends.torrent.private_only).toBe(false);
    expect(backends.torrent.allowed_peer_sources).toBe('Both');
  });

  it('writes coerced numbers, lists and bools and removes empty lists', () => {
    const document = YAML.parseDocument('web: {}\n');
    writeProviderSettings(document, HTTP_KEY, {
      domain_allowlist: 'a.example.com\n\n b.example.com \n',
      max_file_size_bytes: '',
      require_https: 'yes',
      validation_timeout_seconds: '7.9',
    }, getProviderSchema('Http'));
    const http = document.toJS().virtual_soulfind_v2.backends.http;

    expect(http).toEqual({
      domain_allowlist: ['a.example.com', 'b.example.com'],
      require_https: true,
      validation_timeout_seconds: 7,
    });

    writeProviderSettings(document, HTTP_KEY, { domain_allowlist: [] }, [getProviderSchema('Http')[0]]);
    expect(document.toJS().virtual_soulfind_v2.backends.http.domain_allowlist).toBeUndefined();
  });

  it('leaves secrets untouched when blank and writes them when filled', () => {
    const document = YAML.parseDocument([
      'virtual_soulfind_v2:',
      '  backends:',
      '    s3:',
      '      secret_key: keep-me',
    ].join('\n'));
    const schema = getProviderSchema('S3');

    writeProviderSettings(document, providerConfigKey('S3'), { secret_key: '' }, schema);
    expect(document.getIn(['virtual_soulfind_v2', 'backends', 's3', 'secret_key'])).toBe('keep-me');

    writeProviderSettings(document, providerConfigKey('S3'), { endpoint: 'https://s3.example.com', secret_key: 'new' }, schema);
    const s3 = document.toJS().virtual_soulfind_v2.backends.s3;
    expect(s3.secret_key).toBe('new');
    expect(s3.endpoint).toBe('https://s3.example.com');
    expect(s3.region).toBeUndefined();
  });

  it('removes a stored secret that is cleared and keeps the others', () => {
    const document = YAML.parseDocument([
      'virtual_soulfind_v2:',
      '  backends:',
      '    webdav:',
      '      password: keep-me',
      '      bearer_token: old-token',
    ].join('\n'));

    writeProviderSettings(
      document,
      providerConfigKey('WebDav'),
      { bearer_token: null, password: '' },
      getProviderSchema('WebDav').filter((definition) => definition.type === 'secret'),
    );

    expect(document.toJS().virtual_soulfind_v2.backends.webdav).toEqual({ password: 'keep-me' });
  });

  it('writes the float trust score and enum peer sources', () => {
    const document = YAML.parseDocument('');
    writeProviderSettings(document, providerConfigKey('NativeMesh'), { min_trust_score: '0.75' }, getProviderSchema('NativeMesh'));
    writeProviderSettings(document, providerConfigKey('Torrent'), { allowed_peer_sources: 'Overlay' }, getProviderSchema('Torrent'));
    const backends = document.toJS().virtual_soulfind_v2.backends;

    expect(backends.native_mesh.min_trust_score).toBe(0.75);
    expect(backends.torrent.allowed_peer_sources).toBe('Overlay');
  });

  it('reads the live options the server bound, with redacted secrets', () => {
    const live = liveOptionsDocument({
      virtualSoulfindV2: {
        backends: {
          webDav: {
            bearerToken: '*****',
            domainAllowlist: ['dav.example.com'],
            maxFileSizeBytes: 1000,
            validationTimeoutSeconds: 15,
          },
        },
      },
    });
    const { secretsConfigured, values } = readProviderSettings(
      live,
      providerConfigKey('WebDav'),
      getProviderSchema('WebDav'),
    );

    expect(values).toMatchObject({
      bearer_token: '',
      domain_allowlist: ['dav.example.com'],
      max_file_size_bytes: 1000,
      username: '',
    });
    expect(secretsConfigured).toEqual({ bearer_token: true, password: false });
  });

  it('matches the live options only once they hold what the document saved', () => {
    const schema = getProviderSchema('NativeMesh');
    const key = providerConfigKey('NativeMesh');
    const document = YAML.parseDocument('web: {}\n');
    writeProviderSettings(document, key, { max_candidates_per_item: '', min_trust_score: '0.7' }, schema);
    const liveWith = (nativeMesh) => ({ virtualSoulfindV2: { backends: { nativeMesh } } });

    expect(liveSettingsMatch(liveWith({ maxCandidatesPerItem: 20, minTrustScore: 0.3 }), document, key, schema)).toBe(false);
    expect(liveSettingsMatch(null, document, key, schema)).toBe(false);
    // 0.7 comes back as single precision; the removed key is not compared.
    expect(liveSettingsMatch(liveWith({ maxCandidatesPerItem: 99, minTrustScore: 0.699999988079071 }), document, key, schema)).toBe(true);

    const webDav = YAML.parseDocument('web: {}\n');
    writeProviderSettings(webDav, providerConfigKey('WebDav'), { bearer_token: 'new', domain_allowlist: 'dav.example.com' }, getProviderSchema('WebDav'));
    expect(liveSettingsMatch(
      { virtualSoulfindV2: { backends: { webDav: { bearerToken: '*****', domainAllowlist: ['dav.example.com'] } } } },
      webDav,
      providerConfigKey('WebDav'),
      getProviderSchema('WebDav'),
    )).toBe(true);
  });

  it('sets the enabled flag under the provider section', () => {
    const document = YAML.parseDocument('soulseek:\n  username: me\n');
    setProviderEnabled(document, providerConfigKey('Lan'), true);

    expect(document.toJS()).toEqual({
      soulseek: { username: 'me' },
      virtual_soulfind_v2: { backends: { lan: { enabled: true } } },
    });
  });
});
