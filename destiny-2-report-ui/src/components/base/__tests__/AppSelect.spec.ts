import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import AppSelect from '../AppSelect.vue'

describe('AppSelect', () => {
  it('opens a themed listbox and selects an option', async () => {
    const wrapper = mount(AppSelect, {
      props: {
        modelValue: 'raid',
        label: 'Leaderboard',
        options: [
          { value: 'raid', label: 'Raid' },
          { value: 'dungeon', label: 'Dungeon' },
        ],
      },
    })

    await wrapper.get('.select-trigger').trigger('click')
    expect(wrapper.get('[role="listbox"]').isVisible()).toBe(true)

    await wrapper.findAll('[role="option"]')[1]!.trigger('click')
    expect(wrapper.emitted('update:modelValue')).toEqual([['dungeon']])
    expect(wrapper.find('[role="listbox"]').exists()).toBe(false)
  })
})
