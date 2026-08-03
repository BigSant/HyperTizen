#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <tee_client_api.h>

#define CMD_SWU_PASSPHRASE 3
#define CMD_SWU_INIT 0
#define CMD_SWU_UPDATE_AES 1
#define CMD_SWU_FINALIZE_AES 2
#define BUFFER_SIZE 0x10000

static const TEEC_UUID swu_uuid = {
    0x22222221, 0, 0, {0, 0, 0, 0, 0, 0, 0, 1}
};

__attribute__((visibility("default")))
uint32_t hypertizen_probe_swu_open(uint32_t *origin)
{
    TEEC_Context context = {0};
    TEEC_Session session = {0};
    uint32_t local_origin = 0;
    TEEC_Result result = TEEC_InitializeContext(NULL, &context);
    if (result != TEEC_SUCCESS)
        return result;

    result = TEEC_OpenSession(&context, &session, &swu_uuid,
                              TEEC_LOGIN_PUBLIC, NULL, NULL, &local_origin);
    if (origin)
        *origin = local_origin;
    if (result == TEEC_SUCCESS)
        TEEC_CloseSession(&session);
    TEEC_FinalizeContext(&context);
    return result;
}

__attribute__((visibility("default")))
uint32_t hypertizen_probe_swu_unwrap(const uint8_t *mode_data, uint32_t mode_size,
                                    uint8_t *output, uint32_t output_capacity,
                                    uint32_t *output_size, uint32_t *origin,
                                    int32_t *stage)
{
    const char *key_path =
        "/usr/share/org.tizen.tv.swu/itemsAESPassphraseEncrypted.txt";
    TEEC_Context context = {0};
    TEEC_Session session = {0};
    TEEC_SharedMemory input = {0};
    TEEC_SharedMemory result_buffer = {0};
    TEEC_Operation operation = {0};
    uint32_t local_origin = 0;
    TEEC_Result result;
    FILE *file = NULL;
    size_t encrypted_size;

    if (stage) *stage = 0;
    if (output_size) *output_size = 0;
    if (!mode_data || !output || !output_size || mode_size == 0)
        return TEEC_ERROR_BAD_PARAMETERS;

    file = fopen(key_path, "rb");
    if (!file) {
        if (stage) *stage = -errno;
        return TEEC_ERROR_ACCESS_DENIED;
    }

    result = TEEC_InitializeContext(NULL, &context);
    if (result != TEEC_SUCCESS) {
        fclose(file);
        if (stage) *stage = 1;
        return result;
    }

    input.size = BUFFER_SIZE;
    input.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&context, &input);
    if (result != TEEC_SUCCESS) {
        fclose(file);
        TEEC_FinalizeContext(&context);
        if (stage) *stage = 2;
        return result;
    }

    result_buffer.size = BUFFER_SIZE;
    result_buffer.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&context, &result_buffer);
    if (result != TEEC_SUCCESS) {
        fclose(file);
        TEEC_ReleaseSharedMemory(&input);
        TEEC_FinalizeContext(&context);
        if (stage) *stage = 3;
        return result;
    }

    encrypted_size = fread(input.buffer, 1, BUFFER_SIZE, file);
    fclose(file);
    if (encrypted_size == 0 || encrypted_size == BUFFER_SIZE) {
        result = TEEC_ERROR_BAD_FORMAT;
        if (stage) *stage = 4;
        goto cleanup_memory;
    }
    result = TEEC_OpenSession(&context, &session, &swu_uuid,
                              TEEC_LOGIN_PUBLIC, NULL, NULL, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) {
        if (stage) *stage = 5;
        goto cleanup_memory;
    }

    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_INPUT,
        TEEC_MEMREF_PARTIAL_OUTPUT,
        TEEC_VALUE_INOUT,
        TEEC_NONE);
    operation.params[0].memref.parent = &input;
    operation.params[0].memref.size = encrypted_size;
    operation.params[0].memref.offset = 0;
    operation.params[1].memref.parent = &result_buffer;
    operation.params[1].memref.size = BUFFER_SIZE;
    operation.params[1].memref.offset = 0;
    operation.params[2].value.a = mode_data[0];
    operation.params[2].value.b = 0;

    result = TEEC_InvokeCommand(&session, CMD_SWU_PASSPHRASE,
                                &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result == TEEC_SUCCESS) {
        uint32_t copy_size = (uint32_t)operation.params[1].memref.size;
        if (copy_size > output_capacity) copy_size = output_capacity;
        memcpy(output, result_buffer.buffer, copy_size);
        *output_size = copy_size;
        if (stage) *stage = 6;
    } else if (stage) {
        *stage = 7;
    }

    TEEC_CloseSession(&session);

cleanup_memory:
    TEEC_ReleaseSharedMemory(&result_buffer);
    TEEC_ReleaseSharedMemory(&input);
    TEEC_FinalizeContext(&context);
    return result;
}

__attribute__((visibility("default")))
uint32_t hypertizen_probe_swu_decrypt(const uint8_t *salt, uint32_t salt_size,
                                     const uint8_t *ciphertext, uint32_t ciphertext_size,
                                     uint8_t *output, uint32_t output_capacity,
                                     uint32_t *output_size, uint32_t *origin,
                                     int32_t *stage, uint32_t derivation,
                                     uint32_t key_size, uint32_t mode)
{
    const char *key_path =
        "/usr/share/org.tizen.tv.swu/itemsAESPassphraseEncrypted.txt";
    TEEC_Context context = {0};
    TEEC_Session session = {0};
    TEEC_SharedMemory input = {0};
    TEEC_SharedMemory output_memory = {0};
    TEEC_SharedMemory salt_memory = {0};
    TEEC_Operation operation = {0};
    uint32_t local_origin = 0;
    TEEC_Result result = TEEC_ERROR_GENERIC;
    FILE *file = NULL;
    size_t key_bytes = 0;
    uint32_t update_bytes = 0;
    uint32_t final_bytes = 0;

    if (stage) *stage = 0;
    if (output_size) *output_size = 0;
    if (!salt || !ciphertext || !output || !output_size ||
        salt_size == 0 || salt_size > BUFFER_SIZE ||
        ciphertext_size == 0 || ciphertext_size > BUFFER_SIZE)
        return TEEC_ERROR_BAD_PARAMETERS;

    file = fopen(key_path, "rb");
    if (!file) {
        if (stage) *stage = -errno;
        return TEEC_ERROR_ACCESS_DENIED;
    }

    result = TEEC_InitializeContext(NULL, &context);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 1; goto cleanup_file; }

    input.size = BUFFER_SIZE;
    input.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&context, &input);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 2; goto cleanup_context; }

    output_memory.size = BUFFER_SIZE;
    output_memory.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&context, &output_memory);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 3; goto cleanup_input; }

    salt_memory.size = BUFFER_SIZE;
    salt_memory.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&context, &salt_memory);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 4; goto cleanup_output; }

    key_bytes = fread(input.buffer, 1, BUFFER_SIZE, file);
    fclose(file);
    file = NULL;
    if (key_bytes == 0 || key_bytes == BUFFER_SIZE) {
        result = TEEC_ERROR_BAD_FORMAT;
        if (stage) *stage = 5;
        goto cleanup_salt;
    }
    memcpy(salt_memory.buffer, salt, salt_size);

    result = TEEC_OpenSession(&context, &session, &swu_uuid,
                              TEEC_LOGIN_PUBLIC, NULL, NULL, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 6; goto cleanup_salt; }

    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_INPUT, TEEC_MEMREF_PARTIAL_INPUT,
        TEEC_VALUE_INPUT, TEEC_VALUE_INPUT);
    operation.params[0].memref.parent = &input;
    operation.params[0].memref.size = key_bytes;
    operation.params[1].memref.parent = &salt_memory;
    operation.params[1].memref.size = salt_size;
    operation.params[2].value.a = 0; /* decrypt */
    operation.params[2].value.b = 1; /* encrypted passphrase */
    operation.params[3].value.a = derivation;
    operation.params[3].value.b = mode + (key_size << 8);
    result = TEEC_InvokeCommand(&session, CMD_SWU_INIT, &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 7; goto cleanup_session; }

    memcpy(input.buffer, ciphertext, ciphertext_size);
    memset(output_memory.buffer, 0, BUFFER_SIZE);
    memset(&operation, 0, sizeof(operation));
    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_INPUT, TEEC_MEMREF_PARTIAL_OUTPUT,
        TEEC_VALUE_OUTPUT, TEEC_NONE);
    operation.params[0].memref.parent = &input;
    operation.params[0].memref.size = ciphertext_size;
    operation.params[1].memref.parent = &output_memory;
    operation.params[1].memref.size = ciphertext_size;
    result = TEEC_InvokeCommand(&session, CMD_SWU_UPDATE_AES, &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 8; goto cleanup_session; }
    update_bytes = operation.params[2].value.a;
    if (update_bytes > output_capacity) {
        result = TEEC_ERROR_SHORT_BUFFER;
        if (stage) *stage = 9;
        goto cleanup_session;
    }
    memcpy(output, output_memory.buffer, update_bytes);

    memset(output_memory.buffer, 0, BUFFER_SIZE);
    memset(&operation, 0, sizeof(operation));
    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_OUTPUT, TEEC_VALUE_OUTPUT, TEEC_NONE, TEEC_NONE);
    operation.params[0].memref.parent = &output_memory;
    operation.params[0].memref.size = BUFFER_SIZE;
    result = TEEC_InvokeCommand(&session, CMD_SWU_FINALIZE_AES, &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 10; goto cleanup_session; }
    final_bytes = operation.params[1].value.a;
    if (update_bytes + final_bytes > output_capacity) {
        result = TEEC_ERROR_SHORT_BUFFER;
        if (stage) *stage = 11;
        goto cleanup_session;
    }
    memcpy(output + update_bytes, output_memory.buffer, final_bytes);
    *output_size = update_bytes + final_bytes;
    if (stage) *stage = 12;

cleanup_session:
    TEEC_CloseSession(&session);
cleanup_salt:
    TEEC_ReleaseSharedMemory(&salt_memory);
cleanup_output:
    TEEC_ReleaseSharedMemory(&output_memory);
cleanup_input:
    TEEC_ReleaseSharedMemory(&input);
cleanup_context:
    TEEC_FinalizeContext(&context);
cleanup_file:
    if (file) fclose(file);
    return result;
}

typedef struct {
    TEEC_Context context;
    TEEC_Session session;
    TEEC_SharedMemory input;
    TEEC_SharedMemory output;
    TEEC_SharedMemory salt;
    int context_ready;
    int session_ready;
    int active;
} swu_stream_state;

static swu_stream_state swu_stream;

static void swu_stream_cleanup(void)
{
    if (swu_stream.session_ready)
        TEEC_CloseSession(&swu_stream.session);
    if (swu_stream.salt.buffer)
        TEEC_ReleaseSharedMemory(&swu_stream.salt);
    if (swu_stream.output.buffer)
        TEEC_ReleaseSharedMemory(&swu_stream.output);
    if (swu_stream.input.buffer)
        TEEC_ReleaseSharedMemory(&swu_stream.input);
    if (swu_stream.context_ready)
        TEEC_FinalizeContext(&swu_stream.context);
    memset(&swu_stream, 0, sizeof(swu_stream));
}

__attribute__((visibility("default")))
uint32_t hypertizen_probe_swu_stream_begin(
    const uint8_t *salt, uint32_t salt_size, uint32_t *origin, int32_t *stage,
    uint32_t derivation, uint32_t key_size, uint32_t mode)
{
    const char *key_path =
        "/usr/share/org.tizen.tv.swu/itemsAESPassphraseEncrypted.txt";
    TEEC_Operation operation = {0};
    TEEC_Result result;
    uint32_t local_origin = 0;
    FILE *file = NULL;
    size_t key_bytes;

    swu_stream_cleanup();
    if (stage) *stage = 0;
    if (!salt || salt_size == 0 || salt_size > BUFFER_SIZE)
        return TEEC_ERROR_BAD_PARAMETERS;

    file = fopen(key_path, "rb");
    if (!file) { if (stage) *stage = -errno; return TEEC_ERROR_ACCESS_DENIED; }

    result = TEEC_InitializeContext(NULL, &swu_stream.context);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 1; goto fail; }
    swu_stream.context_ready = 1;

    swu_stream.input.size = BUFFER_SIZE;
    swu_stream.input.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&swu_stream.context, &swu_stream.input);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 2; goto fail; }
    swu_stream.output.size = BUFFER_SIZE;
    swu_stream.output.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&swu_stream.context, &swu_stream.output);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 3; goto fail; }
    swu_stream.salt.size = BUFFER_SIZE;
    swu_stream.salt.flags = TEEC_MEM_INPUT | TEEC_MEM_OUTPUT;
    result = TEEC_AllocateSharedMemory(&swu_stream.context, &swu_stream.salt);
    if (result != TEEC_SUCCESS) { if (stage) *stage = 4; goto fail; }

    key_bytes = fread(swu_stream.input.buffer, 1, BUFFER_SIZE, file);
    fclose(file);
    file = NULL;
    if (key_bytes == 0 || key_bytes == BUFFER_SIZE) {
        result = TEEC_ERROR_BAD_FORMAT; if (stage) *stage = 5; goto fail;
    }
    memcpy(swu_stream.salt.buffer, salt, salt_size);

    result = TEEC_OpenSession(&swu_stream.context, &swu_stream.session, &swu_uuid,
                              TEEC_LOGIN_PUBLIC, NULL, NULL, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 6; goto fail; }
    swu_stream.session_ready = 1;

    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_INPUT, TEEC_MEMREF_PARTIAL_INPUT,
        TEEC_VALUE_INPUT, TEEC_VALUE_INPUT);
    operation.params[0].memref.parent = &swu_stream.input;
    operation.params[0].memref.size = key_bytes;
    operation.params[1].memref.parent = &swu_stream.salt;
    operation.params[1].memref.size = salt_size;
    operation.params[2].value.a = 0;
    operation.params[2].value.b = 1;
    operation.params[3].value.a = derivation;
    operation.params[3].value.b = mode + (key_size << 8);
    result = TEEC_InvokeCommand(&swu_stream.session, CMD_SWU_INIT,
                                &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 7; goto fail; }
    swu_stream.active = 1;
    if (stage) *stage = 8;
    return TEEC_SUCCESS;

fail:
    if (file) fclose(file);
    swu_stream_cleanup();
    return result;
}

__attribute__((visibility("default")))
uint32_t hypertizen_probe_swu_stream_update(
    const uint8_t *ciphertext, uint32_t ciphertext_size,
    uint8_t *output, uint32_t output_capacity, uint32_t *output_size,
    uint32_t *origin, int32_t *stage)
{
    TEEC_Operation operation = {0};
    uint32_t local_origin = 0;
    TEEC_Result result;
    uint32_t produced;

    if (stage) *stage = 0;
    if (output_size) *output_size = 0;
    if (!swu_stream.active || !ciphertext || !output || !output_size ||
        ciphertext_size == 0 || ciphertext_size > BUFFER_SIZE)
        return TEEC_ERROR_BAD_PARAMETERS;
    memcpy(swu_stream.input.buffer, ciphertext, ciphertext_size);
    memset(swu_stream.output.buffer, 0, BUFFER_SIZE);
    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_INPUT, TEEC_MEMREF_PARTIAL_OUTPUT,
        TEEC_VALUE_OUTPUT, TEEC_NONE);
    operation.params[0].memref.parent = &swu_stream.input;
    operation.params[0].memref.size = ciphertext_size;
    operation.params[1].memref.parent = &swu_stream.output;
    operation.params[1].memref.size = BUFFER_SIZE;
    result = TEEC_InvokeCommand(&swu_stream.session, CMD_SWU_UPDATE_AES,
                                &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result != TEEC_SUCCESS) { if (stage) *stage = 1; return result; }
    produced = operation.params[2].value.a;
    if (produced > output_capacity) { if (stage) *stage = 2; return TEEC_ERROR_SHORT_BUFFER; }
    memcpy(output, swu_stream.output.buffer, produced);
    *output_size = produced;
    if (stage) *stage = 3;
    return TEEC_SUCCESS;
}

__attribute__((visibility("default")))
uint32_t hypertizen_probe_swu_stream_finish(
    uint8_t *output, uint32_t output_capacity, uint32_t *output_size,
    uint32_t *origin, int32_t *stage)
{
    TEEC_Operation operation = {0};
    uint32_t local_origin = 0;
    TEEC_Result result;
    uint32_t produced = 0;

    if (stage) *stage = 0;
    if (output_size) *output_size = 0;
    if (!swu_stream.active || !output || !output_size)
        return TEEC_ERROR_BAD_PARAMETERS;
    memset(swu_stream.output.buffer, 0, BUFFER_SIZE);
    operation.paramTypes = TEEC_PARAM_TYPES(
        TEEC_MEMREF_PARTIAL_OUTPUT, TEEC_VALUE_OUTPUT, TEEC_NONE, TEEC_NONE);
    operation.params[0].memref.parent = &swu_stream.output;
    operation.params[0].memref.size = BUFFER_SIZE;
    result = TEEC_InvokeCommand(&swu_stream.session, CMD_SWU_FINALIZE_AES,
                                &operation, &local_origin);
    if (origin) *origin = local_origin;
    if (result == TEEC_SUCCESS) {
        produced = operation.params[1].value.a;
        if (produced > output_capacity) {
            result = TEEC_ERROR_SHORT_BUFFER;
            if (stage) *stage = 1;
        } else {
            memcpy(output, swu_stream.output.buffer, produced);
            *output_size = produced;
            if (stage) *stage = 2;
        }
    } else if (stage) {
        *stage = 3;
    }
    swu_stream_cleanup();
    return result;
}

__attribute__((visibility("default")))
void hypertizen_probe_swu_stream_abort(void)
{
    swu_stream_cleanup();
}
