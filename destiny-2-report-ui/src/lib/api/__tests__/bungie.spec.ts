import { describe, expect, it } from 'vitest'
import { bungieUrl } from '../bungie'

describe('bungieUrl', () => {
  it('resolves Bungie-relative image paths', () => {
    expect(bungieUrl('/common/destiny2_content/icons/emblem.jpg')).toBe(
      'https://www.bungie.net/common/destiny2_content/icons/emblem.jpg',
    )
  })

  it('preserves absolute image URLs', () => {
    expect(bungieUrl('https://cdn.example.com/emblem.jpg')).toBe(
      'https://cdn.example.com/emblem.jpg',
    )
  })

  it('returns null when no image path is available', () => {
    expect(bungieUrl(null)).toBeNull()
    expect(bungieUrl('')).toBeNull()
  })
})
