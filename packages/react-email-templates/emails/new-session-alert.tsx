import * as React from 'react'

import {
	AlertContainer,
	Break,
	CtaLink,
	Footer,
	highlightedTextStyle,
	Subtitle,
	Title,
} from '../components/alerts'
import { EmailHtml, HighlightLogo } from '../components/common'

export interface NewSessionAlertEmailProps {
	alertLink?: string
	projectName?: string
	sessionLink?: string
	userIdentifier?: string
}

export const NewSessionAlertEmail = ({
	alertLink = 'https://localhost:3000/1/alerts/sessions/1',
	projectName = 'Highlight Production (app.highlight.io)',
	sessionLink = 'https://localhost:3000/1/sessions/6r5FU4u4SYs4AG4kZjnLHyU5K2N7',
	userIdentifier = '1',
}: NewSessionAlertEmailProps) => (
	<EmailHtml previewText={`${userIdentifier} just started a new session`}>
		<HighlightLogo />
		<Title>
			<span style={highlightedTextStyle}>{userIdentifier}</span> just
			started a new session
		</Title>
		<Subtitle>{projectName}</Subtitle>

		<AlertContainer hideBorder>
			<CtaLink href={sessionLink} label="View session" />
		</AlertContainer>

		<Break />

		<Footer alertLink={alertLink} />
	</EmailHtml>
)

export default NewSessionAlertEmail
