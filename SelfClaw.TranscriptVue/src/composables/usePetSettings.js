import { onMounted, ref } from 'vue';
import { useHostBridge } from './hostBridge.js';

export function usePetSettings() {
    const { request, on } = useHostBridge();
    const petVisible = ref(false);
    const selectedPet = ref('');
    const actualPet = ref('');
    const pets = ref([]);
    const syncPending = ref(false);
    const syncError = ref('');
    let revision = 0;

    function apply(result) {
        if ((result.revision || 0) < revision) return;
        revision = result.revision || 0;
        pets.value = (result.pets || []).map((pet) => ({ ...pet, name: pet.displayName || pet.id,
            desc: pet.description || '', cols: Math.max(1, pet.cols || 1), rows: Math.max(1, pet.rows || 1) }));
        petVisible.value = Boolean(result.isVisible);
        selectedPet.value = result.selectedPetId || '';
        actualPet.value = result.actualPetId || '';
        syncError.value = result.loadError || '';
    }

    async function sync(type, payload = {}) {
        if (syncPending.value) return;
        syncPending.value = true;
        syncError.value = '';
        try { apply(await request(type, payload)); }
        catch (failure) { syncError.value = failure.message; }
        finally { syncPending.value = false; }
    }

    on('pet-settings-changed', apply);
    onMounted(() => sync('get-pet-settings'));
    return { petVisible, selectedPet, actualPet, pets, syncPending, syncError,
        toggleVisible: () => sync('set-pet-visible', { enabled: !petVisible.value }),
        selectPet: (petId) => sync('select-builtin-pet', { petId }) };
}
