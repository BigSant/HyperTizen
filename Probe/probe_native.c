#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <tee_client_api.h>

#define CMD_SWU_PASSPHRASE 3
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
