// One restart action for the header, Settings, banners, and the command palette.

import { configApi } from '../api/config'
import { errorText, toast } from './useToast'

export async function restartQuestX() {
  try {
    await configApi.restartApp()
    toast('Restart sent. QuestX will reconnect shortly.')
  } catch (error) {
    toast(errorText('Restart failed', error), 'error')
  }
}
