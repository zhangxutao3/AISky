PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS forecast_runs (
    id TEXT PRIMARY KEY,
    model TEXT NOT NULL,
    init_time_utc TEXT NOT NULL,
    forecast_time_utc TEXT NOT NULL,
    lead_hours INTEGER NOT NULL,
    source_path TEXT,
    source_file TEXT,
    version TEXT,
    file_size INTEGER NOT NULL DEFAULT 0,
    checksum TEXT,
    state TEXT NOT NULL DEFAULT 'discovered',
    validation_state TEXT NOT NULL DEFAULT 'pending',
    parse_state TEXT NOT NULL DEFAULT 'pending',
    error_message TEXT,
    manifest_path TEXT,
    downloaded_at_utc TEXT,
    last_accessed_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    is_expired INTEGER NOT NULL DEFAULT 0,
    is_plot_ready INTEGER NOT NULL DEFAULT 0,
    created_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (model, init_time_utc, forecast_time_utc)
);

CREATE INDEX IF NOT EXISTS ix_forecast_runs_lookup
    ON forecast_runs (model, init_time_utc DESC, lead_hours);

CREATE INDEX IF NOT EXISTS ix_forecast_runs_ready
    ON forecast_runs (is_plot_ready, model, init_time_utc DESC);

CREATE TABLE IF NOT EXISTS cache_entries (
    cache_key TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES forecast_runs(id) ON DELETE CASCADE,
    layer_id TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    byte_length INTEGER NOT NULL DEFAULT 0,
    checksum TEXT,
    last_accessed_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (run_id, layer_id)
);

CREATE TABLE IF NOT EXISTS download_jobs (
    id TEXT PRIMARY KEY,
    run_id TEXT,
    model TEXT NOT NULL,
    init_time_utc TEXT,
    forecast_time_utc TEXT,
    version TEXT,
    remote_uri TEXT NOT NULL,
    local_path TEXT NOT NULL,
    state TEXT NOT NULL,
    bytes_received INTEGER NOT NULL DEFAULT 0,
    total_bytes INTEGER,
    attempts INTEGER NOT NULL DEFAULT 0,
    error_message TEXT,
    created_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_download_jobs_state
    ON download_jobs (state, updated_at_utc DESC);
