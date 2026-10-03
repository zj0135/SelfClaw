namespace SelfClaw.Infrastructure.Data.Sqlite;

internal static class SqliteSchema
{
    internal static readonly string[] RequiredTables =
    [
        "schema_versions",
        "ai_provider_connections",
        "ai_model_profiles",
        "ai_model_configurations",
        "ai_model_profile_selections",
        "workspace_roots",
        "conversations",
        "git_repositories",
        "git_checkouts",
        "messages",
        "turn_usage",
        "message_attachments",
        "tool_runs",
        "extension_packages",
        "mcp_server_configs",
        "cli_agent_sessions",
        "subagent_tasks",
        "subagent_deliveries",
        "message_segments",
        "conversation_turns",
        "conversation_message_sequences",
        "conversation_inputs",
        "conversation_input_state"
    ];

    internal const string CreateSql = """
        CREATE TABLE schema_versions (
            version INTEGER NOT NULL PRIMARY KEY,
            applied_at_utc TEXT NOT NULL
        );

        CREATE TABLE ai_provider_connections (
            id TEXT NOT NULL PRIMARY KEY,
            catalog_id TEXT NOT NULL DEFAULT 'custom',
            name TEXT NOT NULL,
            provider_kind INTEGER NOT NULL,
            endpoint TEXT NOT NULL,
            auth_kind INTEGER NOT NULL,
            credential_refs_json TEXT NOT NULL DEFAULT '{}',
            connection_options_json TEXT NOT NULL DEFAULT '{}',
            is_enabled INTEGER NOT NULL DEFAULT 1,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE ai_model_profiles (
            id TEXT NOT NULL PRIMARY KEY,
            provider_connection_id TEXT NOT NULL,
            name TEXT NOT NULL,
            api_format INTEGER NOT NULL,
            model TEXT NOT NULL,
            temperature_enabled INTEGER NOT NULL DEFAULT 0,
            temperature REAL NOT NULL DEFAULT 0.7,
            top_p_enabled INTEGER NOT NULL DEFAULT 0,
            top_p REAL NOT NULL DEFAULT 0.7,
            model_options_json TEXT NOT NULL DEFAULT '{}',
            is_enabled INTEGER NOT NULL DEFAULT 1,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            FOREIGN KEY(provider_connection_id) REFERENCES ai_provider_connections(id) ON DELETE CASCADE
        );

        CREATE TABLE ai_model_configurations (
            model TEXT NOT NULL PRIMARY KEY COLLATE BINARY,
            configuration_json TEXT NOT NULL
        );

        CREATE TABLE ai_model_profile_selections (
            scope TEXT NOT NULL PRIMARY KEY,
            model_profile_id TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            FOREIGN KEY(model_profile_id) REFERENCES ai_model_profiles(id) ON DELETE CASCADE
        );

        CREATE TABLE workspace_roots (
            id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            root_path TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE conversations (
            id TEXT NOT NULL PRIMARY KEY,
            title TEXT NOT NULL,
            workspace_root_id TEXT NULL,
            mode INTEGER NOT NULL DEFAULT 0,
            tool_permission_mode INTEGER NOT NULL DEFAULT 0,
            agent_id TEXT NOT NULL DEFAULT 'build',
            channel_kind TEXT NULL,
            channel_conversation_id TEXT NULL,
            channel_display_name TEXT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            kind INTEGER NOT NULL DEFAULT 0,
            parent_conversation_id TEXT NULL,
            CHECK((kind = 0 AND parent_conversation_id IS NULL) OR (kind = 1 AND parent_conversation_id IS NOT NULL)),
            FOREIGN KEY(workspace_root_id) REFERENCES workspace_roots(id) ON DELETE SET NULL,
            FOREIGN KEY(parent_conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
        );

        CREATE TABLE git_repositories (
            id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            common_directory TEXT NOT NULL UNIQUE,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE git_checkouts (
            workspace_root_id TEXT NOT NULL PRIMARY KEY,
            repository_id TEXT NOT NULL,
            is_managed INTEGER NOT NULL DEFAULT 0,
            owner_conversation_id TEXT NULL,
            source_workspace_root_id TEXT NULL,
            branch_name TEXT NOT NULL,
            base_branch_name TEXT NULL,
            base_commit_sha TEXT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            FOREIGN KEY(workspace_root_id) REFERENCES workspace_roots(id) ON DELETE CASCADE,
            FOREIGN KEY(repository_id) REFERENCES git_repositories(id) ON DELETE CASCADE,
            FOREIGN KEY(source_workspace_root_id) REFERENCES workspace_roots(id) ON DELETE SET NULL
        );

        CREATE TABLE messages (
            id TEXT NOT NULL PRIMARY KEY,
            conversation_id TEXT NOT NULL,
            turn_id TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK(sequence > 0),
            role INTEGER NOT NULL CHECK(role BETWEEN 0 AND 2),
            markdown_content TEXT NOT NULL,
            status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 2),
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            agent_id TEXT NULL,
            agent_name TEXT NULL,
            agent_role TEXT NULL,
            UNIQUE(conversation_id, sequence),
            UNIQUE(id, conversation_id),
            UNIQUE(id, turn_id, conversation_id),
            CHECK(id <> turn_id),
            CHECK(role = 2 OR status = 0),
            FOREIGN KEY(turn_id, conversation_id) REFERENCES conversation_turns(id, conversation_id) ON DELETE CASCADE
        );

        CREATE TABLE turn_usage (
            turn_id TEXT NOT NULL PRIMARY KEY,
            conversation_id TEXT NOT NULL,
            model TEXT NULL,
            input_tokens INTEGER NULL,
            uncached_input_tokens INTEGER NULL,
            cached_input_tokens INTEGER NULL,
            cache_write_input_tokens INTEGER NULL,
            output_tokens INTEGER NULL,
            reasoning_tokens INTEGER NULL,
            total_tokens INTEGER NULL,
            provider_calls INTEGER NOT NULL DEFAULT 1,
            context_tokens INTEGER NULL,
            context_window_tokens INTEGER NULL,
            cost_usd_micros INTEGER NULL,
            cost_source INTEGER NOT NULL DEFAULT 0,
            additional_counts_json TEXT NULL,
            created_at_utc TEXT NOT NULL,
            FOREIGN KEY(turn_id, conversation_id) REFERENCES conversation_turns(id, conversation_id) ON DELETE CASCADE
        );

        CREATE TABLE message_attachments (
            id TEXT NOT NULL PRIMARY KEY,
            message_id TEXT NOT NULL,
            kind INTEGER NOT NULL,
            file_name TEXT NOT NULL,
            media_type TEXT NOT NULL,
            storage_path TEXT NOT NULL,
            byte_length INTEGER NOT NULL,
            created_at_utc TEXT NOT NULL,
            FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE
        );

        CREATE TABLE tool_runs (
            id TEXT NOT NULL PRIMARY KEY,
            conversation_id TEXT NOT NULL,
            tool_name TEXT NOT NULL,
            arguments_json TEXT NOT NULL,
            status INTEGER NOT NULL,
            result_summary TEXT NULL,
            result_content TEXT NULL,
            correlation_id TEXT NULL,
            duration_ms REAL NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            agent_id TEXT NULL,
            message_id TEXT NOT NULL,
            source_kind INTEGER NULL,
            source_id TEXT NULL,
            display_name TEXT NULL,
            effective_arguments_json TEXT NULL,
            hook_feedback_json TEXT NULL,
            hook_outcome_json TEXT NULL,
            UNIQUE(id, message_id),
            FOREIGN KEY(message_id, conversation_id) REFERENCES messages(id, conversation_id) ON DELETE CASCADE
        );

        CREATE TABLE extension_packages (
            kind INTEGER NOT NULL,
            id TEXT NOT NULL,
            display_name TEXT NOT NULL,
            version TEXT NOT NULL,
            description TEXT NOT NULL,
            install_path TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            manifest_json TEXT NOT NULL,
            source_plugin_id TEXT NULL,
            is_enabled INTEGER NOT NULL DEFAULT 0,
            acknowledged_permissions_json TEXT NULL,
            acknowledged_at_utc TEXT NULL,
            installed_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            source_path TEXT NULL,
            PRIMARY KEY(kind, id)
        );

        CREATE TABLE mcp_server_configs (
            id TEXT NOT NULL PRIMARY KEY,
            display_name TEXT NOT NULL,
            transport INTEGER NOT NULL,
            settings_json TEXT NOT NULL,
            credential_refs_json TEXT NOT NULL,
            source_plugin_id TEXT NULL,
            is_enabled INTEGER NOT NULL DEFAULT 0,
            config_revision INTEGER NOT NULL DEFAULT 1,
            discovered_tools_json TEXT NOT NULL DEFAULT '[]',
            last_status INTEGER NOT NULL DEFAULT 0,
            last_error TEXT NULL,
            last_checked_at_utc TEXT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE cli_agent_sessions (
            conversation_id TEXT NOT NULL,
            agent_kind INTEGER NOT NULL,
            session_id TEXT NOT NULL,
            cost_baseline_usd_micros INTEGER NOT NULL DEFAULT 0,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            PRIMARY KEY(conversation_id, agent_kind),
            FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
        );

        CREATE TABLE subagent_tasks (
            id TEXT NOT NULL PRIMARY KEY,
            parent_conversation_id TEXT NOT NULL,
            parent_turn_id TEXT NOT NULL,
            child_conversation_id TEXT NOT NULL UNIQUE,
            child_turn_id TEXT NOT NULL UNIQUE,
            subagent_id TEXT NOT NULL,
            subagent_name TEXT NOT NULL,
            task_text TEXT NOT NULL,
            status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 5),
            attempt INTEGER NOT NULL DEFAULT 1 CHECK(attempt >= 1),
            retry_of_task_id TEXT NULL,
            definition_snapshot_json TEXT NOT NULL,
            parent_execution_snapshot_json TEXT NOT NULL,
            resolved_model_profile_id TEXT NULL,
            max_run_seconds INTEGER NOT NULL CHECK(max_run_seconds BETWEEN 30 AND 3600),
            final_text TEXT NULL,
            input_tokens INTEGER NULL,
            output_tokens INTEGER NULL,
            error_code TEXT NULL,
            error_message TEXT NULL,
            cancel_requested_at_utc TEXT NULL,
            queued_at_utc TEXT NOT NULL,
            started_at_utc TEXT NULL,
            completed_at_utc TEXT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            FOREIGN KEY(parent_conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
            FOREIGN KEY(child_conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
            FOREIGN KEY(retry_of_task_id) REFERENCES subagent_tasks(id) ON DELETE SET NULL
        );

        CREATE TABLE subagent_deliveries (
            id TEXT NOT NULL PRIMARY KEY,
            task_id TEXT NOT NULL UNIQUE,
            parent_conversation_id TEXT NOT NULL,
            parent_turn_id TEXT NOT NULL,
            status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 3),
            envelope_json TEXT NOT NULL,
            envelope_bytes INTEGER NOT NULL CHECK(envelope_bytes BETWEEN 0 AND 32768),
            lease_token TEXT NULL,
            leased_until_utc TEXT NULL,
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count BETWEEN 0 AND 3),
            next_attempt_at_utc TEXT NOT NULL,
            continuation_turn_id TEXT NULL,
            last_error TEXT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            delivered_at_utc TEXT NULL,
            dead_lettered_at_utc TEXT NULL,
            tool_execution_started_at_utc TEXT NULL,
            FOREIGN KEY(task_id) REFERENCES subagent_tasks(id) ON DELETE CASCADE,
            FOREIGN KEY(parent_conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
        );

        CREATE TABLE message_segments (
            message_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
            kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 3),
            text TEXT NULL,
            tool_run_id TEXT NULL,
            PRIMARY KEY(message_id, ordinal),
            CHECK((kind = 2 AND tool_run_id IS NOT NULL) OR (kind <> 2 AND tool_run_id IS NULL)),
            FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE,
            FOREIGN KEY(tool_run_id, message_id) REFERENCES tool_runs(id, message_id) DEFERRABLE INITIALLY DEFERRED
        );

        CREATE TABLE conversation_turns (
            id TEXT NOT NULL PRIMARY KEY,
            conversation_id TEXT NOT NULL,
            execution_mode INTEGER NOT NULL CHECK(execution_mode IN (0, 1)),
            origin INTEGER NOT NULL CHECK(origin BETWEEN 0 AND 2),
            status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 6),
            started_at_utc TEXT NOT NULL,
            completed_at_utc TEXT NULL,
            error_message TEXT NULL,
            UNIQUE(id, conversation_id),
            CHECK((status = 0 AND completed_at_utc IS NULL) OR (status <> 0 AND completed_at_utc IS NOT NULL)),
            FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
        );

        CREATE TABLE conversation_message_sequences (
            conversation_id TEXT NOT NULL PRIMARY KEY,
            next_sequence INTEGER NOT NULL CHECK(next_sequence > 0),
            FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
        );

        CREATE TABLE conversation_inputs (
            id TEXT NOT NULL PRIMARY KEY,
            conversation_id TEXT NOT NULL,
            client_request_id TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK(sequence > 0),
            kind INTEGER NOT NULL CHECK(kind IN (0, 1)),
            target_turn_id TEXT NULL,
            payload_json TEXT NOT NULL CHECK(json_valid(payload_json)),
            execution_snapshot_json TEXT NULL CHECK(execution_snapshot_json IS NULL OR json_valid(execution_snapshot_json)),
            status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 4),
            revision INTEGER NOT NULL CHECK(revision > 0),
            claim_id TEXT NULL,
            claim_owner_run_id TEXT NULL,
            consumed_turn_id TEXT NULL,
            message_id TEXT NULL UNIQUE,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            consumed_at_utc TEXT NULL,
            reason_code TEXT NULL,
            error_message TEXT NULL,
            UNIQUE(conversation_id, client_request_id),
            UNIQUE(conversation_id, sequence),
            CHECK((kind = 0 AND target_turn_id IS NULL AND execution_snapshot_json IS NOT NULL) OR
                  (kind = 1 AND target_turn_id IS NOT NULL)),
            CHECK((claim_id IS NULL AND claim_owner_run_id IS NULL) OR
                  (claim_id IS NOT NULL AND claim_owner_run_id IS NOT NULL)),
            CHECK(status NOT IN (1, 2) OR (claim_id IS NOT NULL AND claim_owner_run_id IS NOT NULL)),
            CHECK((status = 2 AND consumed_turn_id IS NOT NULL AND message_id IS NOT NULL AND consumed_at_utc IS NOT NULL) OR
                  (status <> 2 AND consumed_turn_id IS NULL AND message_id IS NULL AND consumed_at_utc IS NULL)),
            CHECK(kind <> 1 OR consumed_turn_id IS NULL OR consumed_turn_id = target_turn_id),
            FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
            FOREIGN KEY(target_turn_id, conversation_id) REFERENCES conversation_turns(id, conversation_id),
            FOREIGN KEY(consumed_turn_id, conversation_id) REFERENCES conversation_turns(id, conversation_id),
            FOREIGN KEY(message_id, consumed_turn_id, conversation_id) REFERENCES messages(id, turn_id, conversation_id)
        );

        CREATE TABLE conversation_input_state (
            conversation_id TEXT NOT NULL PRIMARY KEY,
            next_input_sequence INTEGER NOT NULL CHECK(next_input_sequence > 0),
            queue_revision INTEGER NOT NULL CHECK(queue_revision >= 0),
            paused INTEGER NOT NULL CHECK(paused IN (0, 1)),
            pause_reason TEXT NULL,
            FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
        );

        CREATE INDEX ix_conversations_updated ON conversations(updated_at_utc DESC);
        CREATE INDEX ix_turn_usage_conversation_created ON turn_usage(conversation_id, created_at_utc);
        CREATE INDEX ix_message_attachments_message ON message_attachments(message_id, created_at_utc);
        CREATE INDEX ix_tool_runs_conversation_created ON tool_runs(conversation_id, created_at_utc);
        CREATE INDEX ix_ai_provider_connections_kind ON ai_provider_connections(provider_kind);
        CREATE INDEX ix_ai_model_profiles_connection ON ai_model_profiles(provider_connection_id);
        CREATE INDEX ix_ai_model_profiles_updated ON ai_model_profiles(updated_at_utc DESC);
        CREATE INDEX ix_subagent_tasks_queue ON subagent_tasks(status, queued_at_utc, id);
        CREATE INDEX ix_subagent_tasks_parent_status ON subagent_tasks(parent_conversation_id, status);
        CREATE INDEX ix_subagent_tasks_parent_turn ON subagent_tasks(parent_turn_id, created_at_utc);
        CREATE INDEX ix_subagent_deliveries_ready ON subagent_deliveries(status, next_attempt_at_utc, created_at_utc);
        CREATE INDEX ix_subagent_deliveries_parent_turn ON subagent_deliveries(parent_conversation_id, parent_turn_id, status, created_at_utc);
        CREATE INDEX ix_subagent_deliveries_lease ON subagent_deliveries(status, leased_until_utc);
        CREATE INDEX ix_git_checkouts_repository ON git_checkouts(repository_id);
        CREATE INDEX ix_git_checkouts_owner ON git_checkouts(owner_conversation_id);
        CREATE INDEX ix_messages_turn_sequence ON messages(turn_id, sequence);
        CREATE INDEX ix_conversation_turns_conversation ON conversation_turns(conversation_id, started_at_utc);
        CREATE INDEX ix_conversation_inputs_status ON conversation_inputs(conversation_id, status, kind, sequence);
        CREATE INDEX ix_conversation_inputs_claim ON conversation_inputs(claim_id, claim_owner_run_id);

        CREATE TRIGGER tool_runs_assistant_insert BEFORE INSERT ON tool_runs
        WHEN NOT EXISTS (SELECT 1 FROM messages WHERE id = NEW.message_id AND role = 2)
        BEGIN SELECT RAISE(ABORT, 'A tool requires an actual assistant fragment'); END;
        CREATE TRIGGER tool_runs_assistant_update BEFORE UPDATE OF message_id ON tool_runs
        WHEN NOT EXISTS (SELECT 1 FROM messages WHERE id = NEW.message_id AND role = 2)
        BEGIN SELECT RAISE(ABORT, 'A tool requires an actual assistant fragment'); END;
        CREATE TRIGGER conversation_inputs_interactive_insert BEFORE INSERT ON conversation_inputs
        WHEN NOT EXISTS (SELECT 1 FROM conversations WHERE id = NEW.conversation_id AND kind = 0)
        BEGIN SELECT RAISE(ABORT, 'Input owner must be interactive'); END;
        CREATE TRIGGER conversation_inputs_interactive_update BEFORE UPDATE OF conversation_id ON conversation_inputs
        WHEN NOT EXISTS (SELECT 1 FROM conversations WHERE id = NEW.conversation_id AND kind = 0)
        BEGIN SELECT RAISE(ABORT, 'Input owner must be interactive'); END;
        CREATE TRIGGER conversation_inputs_user_insert BEFORE INSERT ON conversation_inputs
        WHEN NEW.status = 2 AND NOT EXISTS (SELECT 1 FROM messages WHERE id = NEW.message_id AND role = 1)
        BEGIN SELECT RAISE(ABORT, 'Consumed input must map to a user message'); END;
        CREATE TRIGGER conversation_inputs_user_update BEFORE UPDATE ON conversation_inputs
        WHEN NEW.status = 2 AND NOT EXISTS (SELECT 1 FROM messages WHERE id = NEW.message_id AND role = 1)
        BEGIN SELECT RAISE(ABORT, 'Consumed input must map to a user message'); END;
        """;
}
